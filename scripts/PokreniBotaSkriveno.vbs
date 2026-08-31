' Pokrece RewardTracker botove bez vidljivog prozora.
' Putanju do repoa racuna iz sopstvene lokacije, pa fajl radi na svakoj masini.
Option Explicit

Dim fso, shell, koren, bat
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

koren = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName))
bat = fso.BuildPath(koren, "StartBots.bat")

If Not fso.FileExists(bat) Then
    MsgBox "Nije pronadjen fajl:" & vbCrLf & bat, vbCritical, "RewardTracker"
    WScript.Quit 1
End If

shell.Run """" & bat & """", 0, False
