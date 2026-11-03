; Plinth 安装包脚本(NSIS)。由 scripts/publish.ps1 调用,参数:
;   VERSION      版本号
;   PUBLISH_DIR  dotnet publish 的输出文件夹(绝对路径)
;
; 装到当前用户自己的目录(%LOCALAPPDATA%\Programs\Plinth),不需要管理员权限。
; 卸载只删发布清单中的程序文件和快捷方式，保留未知文件与用户数据。

Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

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
  IfFileExists "$INSTDIR\Plinth.App.exe" upgrade_prompt 0
  ; 旧安装失败后 EXE 可能缺失，仍不能视为首次静默安装。
  IfFileExists "$INSTDIR\Uninstall.exe" upgrade_prompt 0
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "DisplayVersion"
  StrCmp $0 "" install_begin upgrade_prompt
  upgrade_prompt:
    ; 静默升级没有人工备份确认，保守拒绝；首次静默安装仍可用。
    IfSilent upgrade_silent
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "即将覆盖已有程序。升级前请用旧版的“备份与恢复”创建 ZIP，保存在安装目录及数据目录之外，并记录实际保存位置。默认数据目录：$LOCALAPPDATA\Plinth；PLINTH_DATA_DIR 可改变它。$\r$\n$\r$\n如尚未备份，请取消安装，回到旧版完成备份。确认备份后完全退出所有窗口，再点确定继续。本安装器不会直接复制数据库，也不会删除用户数据。" IDCANCEL upgrade_cancel
    Goto install_begin
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
