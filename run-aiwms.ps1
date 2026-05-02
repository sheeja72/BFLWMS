Set-Location "D:\AI Projects\Aiwms\src\Aiwms.Web"
taskkill /F /IM Aiwms.Web.exe 2>$null
dotnet watch run