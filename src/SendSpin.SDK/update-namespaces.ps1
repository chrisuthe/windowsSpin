$files = Get-ChildItem -Path . -Recurse -Filter "*.cs"
foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw
    $updated = $content -replace 'SendSpinClient\.Core', 'SendSpin.SDK'
    Set-Content $file.FullName $updated -NoNewline
    Write-Host "Updated: $($file.Name)"
}
Write-Host "Done!"
