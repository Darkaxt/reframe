# Waits (max 120 s) for an interactive guest logon: explorer.exe in a non-zero session.
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $e = Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }
    if ($e) { Start-Sleep -Seconds 5; "logon ready: explorer session $($e[0].SessionId)"; exit 0 }
    Start-Sleep -Seconds 1
}
'no interactive logon within 120 s'; exit 3
