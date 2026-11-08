# Plinth 任务完成通知。由 TaskNotifier 以隐藏窗口调用；只接收固定状态文本，不接触业务数据。
# 失败时退出码非 0，但调用方不依赖它，也不会改变任务结果。
param(
    [Parameter(Mandatory = $true)][string]$Aumid,
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$Body,
    [string]$LaunchUri = 'plinth://tasks'
)

$ErrorActionPreference = 'Stop'

try {
    # unpackaged 应用的应用标识；安装器也会写同一项，便携包首次通知时自建。
    $identity = "HKCU:\Software\Classes\AppUserModelId\$Aumid"
    try {
        New-Item -Path $identity -Force | Out-Null
        Set-ItemProperty $identity -Name DisplayName -Value $Title
        Set-ItemProperty $identity -Name ShowInSettings -Value 1
    } catch {
        # 注册表被策略锁定时仍尝试发送；已由安装器注册时不需要再写。
    }

    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null

    $escape = [System.Security.SecurityElement]
    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $xml.LoadXml(
        '<toast activationType="protocol" launch="' + $escape::Escape($LaunchUri) + '">' +
        '<visual><binding template="ToastGeneric"><text>' + $escape::Escape($Title) + '</text><text>' +
        $escape::Escape($Body) + '</text></binding></visual></toast>')

    $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($Aumid).Show($toast)
    exit 0
} catch {
    exit 1
}
