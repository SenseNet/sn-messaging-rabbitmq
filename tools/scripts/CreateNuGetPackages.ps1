$srcPath = [System.IO.Path]::GetFullPath(($PSScriptRoot + '\..\..\src'))

# delete existing packages
Remove-Item $PSScriptRoot\*.nupkg

nuget pack $srcPath\SenseNet.Messaging.RabbitMQ\SenseNet.Messaging.RabbitMQ.nuspec -properties Configuration=Release -OutputDirectory $PSScriptRoot