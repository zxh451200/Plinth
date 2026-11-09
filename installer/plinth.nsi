; Plinth 安装包脚本(NSIS)。由 scripts/publish.ps1 调用,参数:
;   VERSION      版本号
;   PUBLISH_DIR  dotnet publish 的输出文件夹(绝对路径)
;
; 装到当前用户自己的目录(%LOCALAPPDATA%\Programs\Plinth),不需要管理员权限。
; 卸载只删发布清单中的程序文件和快捷方式，保留未知文件与用户数据。

Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef PUBLISH_DIR
  !error "缺少 PUBLISH_DIR 参数,请用 scripts/publish.ps1 调用"
!endif
!ifndef OUTPUT_FILE
  !error "缺少 OUTPUT_FILE 参数,请用 scripts/publish.ps1 调用"
!endif
!ifndef UNINSTALL_MANIFEST
  !error "缺少 UNINSTALL_MANIFEST 参数,请用 scripts/publish.ps1 调用"
!endif

!define APP_NAME "Plinth"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

Name "${APP_NAME}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Var InstallationLock
Var ExecutableLock
Var PreviousExecutable
Var RemoveInstallationLock
Var InstallationStarted
Var UpgradeBackupPath
Var UpgradeHelper
Var UpgradeBackupInfo
!include "${UNINSTALL_MANIFEST}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; 同一文件上的共享/独占句柄：多数据目录实例可同时读，安装/卸载必须独占。
; 不删除或替换此文件，否则旧进程的句柄将不能保护新文件。
!macro InstallationFunctions PREFIX
Function ${PREFIX}CheckInstallDirectory
  ; /D= 不允许改变固定安装路径;直接比较规范化后的 $INSTDIR。
  StrCpy $0 "$LOCALAPPDATA\Programs\${APP_NAME}"
  StrCmp $INSTDIR $0 directory_ok
    MessageBox MB_OK|MB_ICONSTOP "安装位置必须为当前用户的 $0。已停止操作。" /SD IDOK
    SetErrorLevel 4
    Quit
  directory_ok:
  ; 拒绝 Programs 或目标目录的 junction/symlink，避免固定路径指向其他数据。
  System::Call 'kernel32::GetFileAttributesW(w "$LOCALAPPDATA\Programs") i.r0'
  IntCmpU $0 0xFFFFFFFF check_target
  IntOp $0 $0 & 0x400
  IntCmp $0 0 check_target unsafe_directory unsafe_directory
  check_target:
  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i.r0'
  IntCmpU $0 0xFFFFFFFF directory_done
  IntOp $0 $0 & 0x400
  IntCmp $0 0 directory_done unsafe_directory unsafe_directory
  unsafe_directory:
    MessageBox MB_OK|MB_ICONSTOP "安装目录不能使用目录链接。已停止操作。" /SD IDOK
    SetErrorLevel 4
    Quit
  directory_done:
FunctionEnd

Function ${PREFIX}AcquireInstallationLock
  Call ${PREFIX}CheckInstallDirectory
  CreateDirectory "$INSTDIR"
  ; GENERIC_READ，FILE_SHARE_DELETE，OPEN_ALWAYS。允许卸载删除锁文件，
  ; 但不允许其他读句柄（应用或另一安装器）进入。
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\plinth.install.lock", i 0x80000000, i 4, p 0, i 4, i 0x80, p 0) p.r0 ?e'
  Pop $1
  StrCpy $InstallationLock $0
  IntCmp $InstallationLock -1 lock_failed
  ; ERROR_ALREADY_EXISTS=183。新建的 gate 只在完整安装成功后保留。
  StrCpy $RemoveInstallationLock ""
  IntCmp $1 183 gate_ready
    StrCpy $RemoveInstallationLock "1"
  gate_ready:
  ; 兼容未使用锁文件的旧版本：持有旧 EXE 的写句柄直到本次操作结束。
  ; 不强杀进程，不请求下次重启再覆盖。
  IfFileExists "$INSTDIR\Plinth.App.exe" 0 lock_acquired
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\Plinth.App.exe", i 0xC0000000, i 7, p 0, i 3, i 0x80, p 0) p.r0'
  StrCpy $ExecutableLock $0
  IntCmp $ExecutableLock -1 lock_failed lock_acquired lock_acquired
  lock_failed:
    Call ${PREFIX}ReleaseInstallationLock
    MessageBox MB_OK|MB_ICONSTOP "无法锁定程序文件。请完全退出此安装目录中的所有 Plinth 窗口及其他安装器，并检查目录权限后重试。没有终止任何进程。" /SD IDOK
    SetErrorLevel 5
    Quit
  lock_acquired:
FunctionEnd

Function ${PREFIX}ReleaseInstallationLock
  ${If} $ExecutableLock != ""
  ${AndIf} $ExecutableLock != -1
    System::Call 'kernel32::CloseHandle(p $ExecutableLock)'
    StrCpy $ExecutableLock ""
  ${EndIf}
  ${If} $InstallationLock != ""
  ${AndIf} $InstallationLock != -1
    ${If} $RemoveInstallationLock == "1"
      Delete "$INSTDIR\plinth.install.lock"
      StrCpy $RemoveInstallationLock ""
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p $InstallationLock)'
    StrCpy $InstallationLock ""
  ${EndIf}
FunctionEnd
!macroend

!insertmacro InstallationFunctions ""
!insertmacro InstallationFunctions "un."

Function .onInit
  Call CheckInstallDirectory
  ; /D= 不得改变固定安装路径；不从可修改的卸载注册表接受删除目标。
FunctionEnd

Function .onGUIEnd
  Call ReleaseInstallationLock
  ; 不让工作目录占用临时助手目录，便于 NSIS 退出时清理。
  SetOutPath "$TEMP"
FunctionEnd

Function .onInstFailed
  ; 缺少交付锁文件时新版本拒绝启动，防止用户打开未完成的混合版本。
  ${If} $InstallationStarted == "1"
    Delete "$INSTDIR\plinth.install.lock"
  ${EndIf}
  Call ReleaseInstallationLock
  MessageBox MB_OK|MB_ICONSTOP "安装未完成。用户数据未删除。程序目录可能包含未完成的版本，请保持应用关闭并重新运行完整安装包，不要手工替换数据库。" /SD IDOK
  SetErrorLevel 7
FunctionEnd

Function un.onInit
  Call un.CheckInstallDirectory
FunctionEnd

Function un.onGUIEnd
  Call un.ReleaseInstallationLock
FunctionEnd

Section "Install"
  Call AcquireInstallationLock
  !insertmacro CheckPublishedPaths install_failed
  IfFileExists "$INSTDIR\Plinth.App.exe" upgrade_prompt 0
  ; 旧安装失败后 EXE 可能缺失，仍不能视为首次静默安装。
  IfFileExists "$INSTDIR\Uninstall.exe" upgrade_prompt 0
  ; 已有交付锁文件也说明是升级/修复；本次新建锁不能误判为已有安装。
  StrCmp $RemoveInstallationLock "" upgrade_prompt 0
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "DisplayVersion"
  StrCmp $0 "" install_begin upgrade_prompt
  upgrade_prompt:
    ; 保留既有策略：静默升级一律拒绝，自动备份不能成为绕过入口。
    IfSilent upgrade_silent
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "请先保存业务修改并完全退出应用。安装器将尝试调用旧版创建升级前 ZIP；历史版不支持或程序缺失时，必须选择此前由旧应用创建的 ZIP 并通过新版校验。仅处理本进程 PLINTH_DATA_DIR 指定的数据目录（默认 $LOCALAPPDATA\Plinth），其他数据目录请分别备份。是否继续？" IDCANCEL upgrade_cancel
    ; 完整可信发布依赖只解压到 NSIS 私有临时目录，不加载安装目录的 DLL。
    ClearErrors
    InitPluginsDir
    IfErrors upgrade_backup_failed
    StrCpy $UpgradeHelper "$PLUGINSDIR\upgrade-helper"
    ClearErrors
    SetOutPath "$UpgradeHelper"
    IfErrors upgrade_backup_failed
    File /r /x plinth.install.lock "${PUBLISH_DIR}\*.*"
    IfErrors upgrade_backup_failed
    ; deployment.json 不随包交付；必须保留旧部署的授权/生产/数据库策略。
    System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR\deployment.json") i.r0 ?e'
    Pop $1
    IntCmpU $0 0xFFFFFFFF upgrade_deployment_missing upgrade_copy_deployment upgrade_copy_deployment
  upgrade_deployment_missing:
    ; 仅文件/父目录不存在才采用默认值；访问被拒绝等错误不能降级策略。
    IntCmp $1 2 upgrade_no_deployment
    IntCmp $1 3 upgrade_no_deployment upgrade_backup_failed upgrade_backup_failed
  upgrade_copy_deployment:
    IntOp $1 $0 & 0x410
    IntCmp $1 0 0 upgrade_backup_failed upgrade_backup_failed
    ClearErrors
    CopyFiles /SILENT "$INSTDIR\deployment.json" "$UpgradeHelper\deployment.json"
    IfErrors upgrade_backup_failed
  upgrade_no_deployment:
    IfFileExists "$INSTDIR\Plinth.App.exe" 0 upgrade_select_backup
    CreateDirectory "$LOCALAPPDATA\Plinth-Upgrade-Backups"
    ClearErrors
    GetTempFileName $UpgradeBackupPath "$LOCALAPPDATA\Plinth-Upgrade-Backups"
    IfErrors upgrade_backup_failed
    ClearErrors
    Delete "$UpgradeBackupPath"
    IfErrors upgrade_backup_failed
    StrCpy $UpgradeBackupPath "$UpgradeBackupPath.zip"
    ; GetTempFileName 只保留原临时名，不保证追加 .zip 后仍是新路径。
    ; 不允许旧版未执行命令却以 0 退出时，把已有 ZIP 误判为本次备份。
    IfFileExists "$UpgradeBackupPath" upgrade_backup_failed 0
    ; 写句柄会阻止 Windows 启动旧 EXE。目录独占锁在整个调用期间保持。
    System::Call 'kernel32::CloseHandle(p $ExecutableLock)'
    StrCpy $ExecutableLock ""
    ClearErrors
    ExecWait '"$INSTDIR\Plinth.App.exe" --backup-before-upgrade "$UpgradeBackupPath"' $0
    IfErrors upgrade_select_backup
    IntCmp $0 0 upgrade_auto_result upgrade_select_backup upgrade_select_backup
  upgrade_auto_result:
    ; 某些历史版可能忽略参数并返回 0；没有新 ZIP 时仍可选择既有备份。
    IfFileExists "$UpgradeBackupPath" upgrade_verify_backup upgrade_select_backup
  upgrade_select_backup:
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "旧版自动备份未完成或旧 EXE 缺失。请选择已由旧应用创建的最终 ZIP 备份。若尚未备份，请取消安装；修复安装也必须校验备份。不会忽略失败直接覆盖。" IDCANCEL upgrade_cancel
    ; 官方 nsDialogs API：mode, initial_selection, filter；取消返回空字符串。
    nsDialogs::SelectFileDialog open "" "Plinth ZIP backup|*.zip"
    Pop $UpgradeBackupPath
    StrCmp $UpgradeBackupPath "" upgrade_cancel
  upgrade_verify_backup:
    ClearErrors
    ExecWait '"$UpgradeHelper\Plinth.App.exe" --inspect-backup-before-upgrade "$UpgradeBackupPath" "$UpgradeHelper\backup-report.txt"' $0
    IfErrors upgrade_backup_failed
    IntCmp $0 0 upgrade_backup_ready upgrade_backup_failed upgrade_backup_failed
  upgrade_backup_ready:
    ; 助手成功意味着 manifest/大小/hash/数据库完整性和兼容性全部通过。
    ClearErrors
    FileOpen $1 "$UpgradeHelper\backup-report.txt" r
    IfErrors upgrade_backup_failed
    FileReadUTF16LE $1 $2
    FileReadUTF16LE $1 $3
    FileReadUTF16LE $1 $4
    FileClose $1
    IfErrors upgrade_backup_failed
    StrCpy $UpgradeBackupInfo "$2$3$4"
    ; 子进程已退出，重新检查旧版本占用；仍不强杀或安排重启覆盖。
    IfFileExists "$INSTDIR\Plinth.App.exe" 0 upgrade_backup_confirm
    ${If} $ExecutableLock != ""
      System::Call 'kernel32::CloseHandle(p $ExecutableLock)'
      StrCpy $ExecutableLock ""
    ${EndIf}
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\Plinth.App.exe", i 0xC0000000, i 7, p 0, i 3, i 0x80, p 0) p.r0'
    StrCpy $ExecutableLock $0
    IntCmp $ExecutableLock -1 upgrade_backup_failed
  upgrade_backup_confirm:
    MessageBox MB_OKCANCEL|MB_ICONINFORMATION "备份校验通过：$UpgradeBackupPath$\r$\n$UpgradeBackupInfo$\r$\n请确认该备份属于本次升级的数据目录，且包含最后保存的修改。清单无法证明它属于当前目录或是最新快照。确认后将替换程序文件。" IDCANCEL upgrade_cancel
    Goto install_begin
  upgrade_backup_failed:
    Call ReleaseInstallationLock
    SetErrorLevel 6
    MessageBox MB_OK|MB_ICONSTOP "升级前备份校验、授权或程序占用检查失败，已停止覆盖。可能生成的备份保留在 $LOCALAPPDATA\Plinth-Upgrade-Backups。请检查 ZIP、磁盘、权限和部署配置。生产确认、身份授权及服务器数据库限制不能通过手选备份绕过。" /SD IDOK
    Quit
  upgrade_silent:
    Call ReleaseInstallationLock
    SetErrorLevel 6
    Quit
  upgrade_cancel:
    Call ReleaseInstallationLock
    SetErrorLevel 1
    Quit
  install_begin:
  !insertmacro CheckPublishedPaths install_failed
  StrCpy $InstallationStarted "1"
  ; 旧 EXE 的写句柄不能与 NSIS 解包句柄共享，先把它移到独立临时路径。
  ; 不覆盖旧文件；安装失败时保留，重装成功后再清理本次旧 EXE。
  IfFileExists "$INSTDIR\Plinth.App.exe" 0 extract_files
  ClearErrors
  GetTempFileName $PreviousExecutable "$INSTDIR"
  IfErrors install_failed
  Delete "$PreviousExecutable"
  ClearErrors
  Rename "$INSTDIR\Plinth.App.exe" "$PreviousExecutable"
  IfErrors install_failed
  extract_files:
  ; XLSX 关闭时清理旧版残留的 ClosedXML 家族，避免升级后继续携带 Carlito。
  ; 新包包含 ClosedXML 时跳过，交给正常文件覆盖处理。
  !if /FileExists "${PUBLISH_DIR}\ClosedXML.dll"
  !else
    ClearErrors
    Delete "$INSTDIR\ClosedXML.dll"
    Delete "$INSTDIR\ClosedXML.Parser.dll"
    Delete "$INSTDIR\DocumentFormat.OpenXml.dll"
    Delete "$INSTDIR\DocumentFormat.OpenXml.Framework.dll"
    Delete "$INSTDIR\ExcelNumberFormat.dll"
    IfErrors install_failed
  !endif
  SetOutPath "$INSTDIR"
  SetOverwrite on
  ClearErrors
  File /r /x plinth.install.lock "${PUBLISH_DIR}\*.*"
  IfErrors install_failed
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors install_failed

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\Plinth.App.exe"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ; 系统通知身份与点击入口（当前用户，不需要管理员）。通知脚本也写同一项，供便携包使用。
  WriteRegStr HKCU "Software\Classes\AppUserModelId\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Classes\AppUserModelId\${APP_NAME}" "IconUri" "file:///$INSTDIR/Plinth.App.exe"
  WriteRegDWORD HKCU "Software\Classes\AppUserModelId\${APP_NAME}" "ShowInSettings" 1
  WriteRegStr HKCU "Software\Classes\plinth" "" "URL:${APP_NAME} 任务中心"
  WriteRegStr HKCU "Software\Classes\plinth" "URL Protocol" ""
  WriteRegStr HKCU "Software\Classes\plinth\shell\open\command" "" "$\"$INSTDIR\Plinth.App.exe$\" $\"plinth://tasks$\""
  IfErrors install_failed
  StrCpy $RemoveInstallationLock ""
  Call ReleaseInstallationLock
  ${If} $PreviousExecutable != ""
    Delete "$PreviousExecutable"
  ${EndIf}
  Goto install_done
  install_failed:
    SetErrorLevel 7
    Abort
  install_done:
SectionEnd

Section "Uninstall"
  Call un.AcquireInstallationLock
  ; 只删本版本交付的文件，不遍历或递归删除未知目录。
  SetOutPath "$TEMP"
  ClearErrors
  !insertmacro RemovePublishedFiles
  IfErrors uninstall_failed_locked
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\plinth.install.lock"
  IfErrors uninstall_failed_locked
  ; 锁文件可能在句柄关闭前保持待删除状态，关闭后只移除空目录。
  Call un.ReleaseInstallationLock
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ; 只删除本应用写入的通知身份，以及仍指向本安装目录的任务中心协议。
  DeleteRegKey HKCU "Software\Classes\AppUserModelId\${APP_NAME}"
  ReadRegStr $0 HKCU "Software\Classes\plinth\shell\open\command" ""
  StrCmp $0 "$\"$INSTDIR\Plinth.App.exe$\" $\"plinth://tasks$\"" 0 +2
    DeleteRegKey HKCU "Software\Classes\plinth"
  ; 用户数据(%LOCALAPPDATA%\Plinth)保留,重装后还在
  Goto uninstall_done
  uninstall_failed_locked:
    Call un.ReleaseInstallationLock
  uninstall_failed:
    MessageBox MB_OK|MB_ICONSTOP "部分程序文件无法删除。用户数据已保留。请关闭占用程序并重试卸载。" /SD IDOK
    SetErrorLevel 7
    Abort
  uninstall_done:
SectionEnd
