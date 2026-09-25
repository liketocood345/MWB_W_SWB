Set sh = CreateObject("WScript.Shell")
exeLocal = sh.ExpandEnvironmentStrings("%LOCALAPPDATA%\MWB-SWB-Host\MwbSwb.Host.exe")
exeAdmin = "C:\Users\Administrator\AppData\Local\MWB-SWB-Host\MwbSwb.Host.exe"
exePublic = "C:\Users\Public\MWB-SWB-Host\MwbSwb.Host.exe"
exe = ""
Set fso = CreateObject("Scripting.FileSystemObject")
If fso.FileExists(exeLocal) Then
  exe = exeLocal
ElseIf fso.FileExists(exeAdmin) Then
  exe = exeAdmin
ElseIf fso.FileExists(exePublic) Then
  exe = exePublic
End If
If exe = "" Then
  MsgBox "MwbSwb.Host.exe not found under LocalAppData\MWB-SWB-Host", 48, "Launch SWB"
  WScript.Quit 1
End If
' Window style 1 = normal; False = do not wait
sh.Run """" & exe & """ /open-swb", 1, False