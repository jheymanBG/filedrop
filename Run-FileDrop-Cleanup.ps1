$ErrorActionPreference = "Stop"
Invoke-WebRequest -Uri "http://localhost:8080/Admin/RunCleanup" -Method Post -UseBasicParsing | Out-Null
