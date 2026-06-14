Set UAC = CreateObject("Shell.Application")
Set FSO = CreateObject("Scripting.FileSystemObject")

' Get absolute path to the directory containing this script
strPath = FSO.GetParentFolderName(WScript.ScriptFullName)

' Check if we are already elevated
If WScript.Arguments.Named.Exists("elevated") Then
    ' We are elevated, run the batch file hidden
    Set WshShell = CreateObject("WScript.Shell")
    WshShell.CurrentDirectory = strPath
    WshShell.Run "cmd /c """ & strPath & "\run.bat""", 0, False
Else
    ' Request elevation
    UAC.ShellExecute "wscript.exe", """" & WScript.ScriptFullName & """ /elevated", strPath, "runas", 1
End If