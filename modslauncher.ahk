; This is some shit I made to bypass RGL automatically replacing files when you launch GTA 5.
#Requires AutoHotkey v2.1-alpha
#SingleInstance Force
#NoTrayIcon

if !A_IsAdmin {
    Run('*RunAs "' A_AhkPath '" "' A_ScriptFullPath '"')
    ExitApp
}

global gtaFolder := A_WorkingDir
global movedFiles := []
isEnhanced := false

; If the working directory itself is already the GTA folder
if RegExMatch(gtaFolder, "i)\\Grand Theft Auto V Enhanced$") {
    isEnhanced := true
} else if RegExMatch(gtaFolder, "i)\\(Grand Theft Auto V Legacy|GTA V)$") {
    ; gtaFolder is already correct
} else if FileExist(gtaFolder "\Grand Theft Auto V Enhanced") {
    gtaFolder .= "\Grand Theft Auto V Enhanced"
    isEnhanced := true
} else if FileExist(gtaFolder "\Grand Theft Auto V Legacy") {
    gtaFolder .= "\Grand Theft Auto V Legacy"
} else if FileExist(gtaFolder "\GTA V") {
    gtaFolder .= "\GTA V"
} else {
    MsgBox("Couldn't find GTA V folder")
    ExitApp
}
executableName := isEnhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"

theGui := Gui()
launchButton := theGui.AddButton("w400 h200", "Launch the game")
launchButton.OnEvent("Click", runGTA)
hideWindowButton := theGui.AddButton("w400 h200", "Hide this window")
hideWindowButton.OnEvent("Click", (*) {
    if (ProcessExist(executableName)) {
        MsgBox("The window will be hidden. This launcher will still run in the background until GTA closes.")
        theGui.Hide()
    } else {
        MsgBox("GTA is not running. You can only hide this window while GTA is running.")
    }
})
theGui.OnEvent("Close", (*) {
    if (exitShit()) {
        return 1
    }
    ExitApp()
})
theGui.Show()

; OnExit(exitShit)

exitShit(*) {
    if (ProcessExist(executableName)) {
        answer := MsgBox("GTA5.exe is still running. Are you sure you want to quit? GTA will be force closed.", "Warning", 4)
        if (answer == "No") {
            return 1
        }
        ProcessClose(executableName)
    }
    restoreMods()
    return 0
}

runGTA(*) {
    ; I can't remember why I used GTAVLauncher.exe, I swear PlayGTAV.exe didn't work, but now it does work on Legacy. Idfk.
    ; Run(gtaFolder (isEnhanced ? "\PlayGTAV.exe" : "\GTAVLauncher.exe"))
    Run(gtaFolder "\PlayGTAV.exe")

    ProcessWait(executableName)

    moveMods()

    ProcessWaitClose(executableName)

    restoreMods()
}

moveMods() {
    global movedFiles

    modsFolder := gtaFolder "\mods"

    loop files modsFolder "\*", "FR" {
        source := A_LoopFileFullPath
        relative := SubStr(source, StrLen(modsFolder) + 2)
        target := gtaFolder "\" relative
        backup := target ".bak"

        SplitPath(target, , &targetDir)
        DirCreate(targetDir)

        hadOriginal := FileExist(target)

        if hadOriginal
            FileMove(target, backup, 1)

        ; Remember everything needed to undo this later
        movedFiles.Push({
            source: source,
            target: target,
            backup: backup,
            hadOriginal: !!hadOriginal
        })

        FileMove(source, target, 1)
    }
}

restoreMods() {
    global movedFiles

    loop movedFiles.Length {
        file := movedFiles[movedFiles.Length - A_Index + 1]

        SplitPath(file.source, , &sourceDir)
        DirCreate(sourceDir)

        if FileExist(file.target)
            FileMove(file.target, file.source, 1)

        if file.hadOriginal && FileExist(file.backup)
            FileMove(file.backup, file.target, 1)
    }

    movedFiles := []
}
