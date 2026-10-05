# Disposable visible window for testing layout save/restore without moving user apps.
Add-Type -AssemblyName System.Windows.Forms

$form = New-Object System.Windows.Forms.Form
$form.Text = 'MPS Layout Smoke Test'
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = New-Object System.Drawing.Rectangle -ArgumentList 120, 120, 420, 260
$form.ShowInTaskbar = $true
[System.Windows.Forms.Application]::Run($form)
