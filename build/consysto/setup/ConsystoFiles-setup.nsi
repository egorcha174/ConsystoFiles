; Сборка: ..\Build-ConsystoSetup.ps1. Устанавливается только содержимое app, без data.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef PAYLOAD
  !error "PAYLOAD required"
!endif
!ifndef OUTPUT
  !error "OUTPUT required"
!endif
!ifndef VERSION
  !error "VERSION required"
!endif
!ifndef ESTIMATED_SIZE
  !error "ESTIMATED_SIZE required"
!endif
!ifndef PROCESS_CHECK
  !error "PROCESS_CHECK required"
!endif

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ConsystoFiles"
Name "Consysto Files"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Consysto Files"
RequestExecutionLevel user
SetCompressor /SOLID lzma
AllowSkipFiles off
VIProductVersion "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "Consysto Files"
VIAddVersionKey /LANG=1033 "FileDescription" "Consysto Files Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Egor Chayka"
VIAddVersionKey /LANG=1049 "ProductName" "Consysto Files"
VIAddVersionKey /LANG=1049 "FileDescription" "Установщик Consysto Files"
VIAddVersionKey /LANG=1049 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Egor Chayka"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!define MUI_COMPONENTSPAGE_NODESC
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Files.exe"
!define MUI_FINISHPAGE_RUN_TEXT "$(RunText)"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
; NSIS выбирает язык интерфейса Windows; для остальных языков используется English.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"

LangString WelcomeText ${LANG_RUSSIAN} "Установка Consysto Files ${VERSION} для текущего пользователя.$\r$\n$\r$\nПапка: $LOCALAPPDATA\Programs\Consysto Files$\r$\n$\r$\nПеред обновлением закройте установленную копию Consysto Files. Настройки и данные в папке data сохранятся."
LangString WelcomeText ${LANG_ENGLISH} "Install Consysto Files ${VERSION} for the current user.$\r$\n$\r$\nFolder: $LOCALAPPDATA\Programs\Consysto Files$\r$\n$\r$\nClose the installed copy of Consysto Files before updating. Settings and files in the data folder will be preserved."
LangString AppSection ${LANG_RUSSIAN} "Программа и ярлык в меню «Пуск»"
LangString AppSection ${LANG_ENGLISH} "Application and Start menu shortcut"
LangString DesktopSection ${LANG_RUSSIAN} "Ярлык на рабочем столе"
LangString DesktopSection ${LANG_ENGLISH} "Desktop shortcut"
LangString RunText ${LANG_RUSSIAN} "Запустить Consysto Files"
LangString RunText ${LANG_ENGLISH} "Run Consysto Files"
LangString CloseFiles ${LANG_RUSSIAN} "Consysto Files запущен из папки установки. Закройте его и нажмите «Повторить»."
LangString CloseFiles ${LANG_ENGLISH} "Consysto Files is running from the installation folder. Close it and click Retry."
LangString ProcessCheckFailed ${LANG_RUSSIAN} "Не удалось проверить запущенные копии Files.exe. Операция остановлена."
LangString ProcessCheckFailed ${LANG_ENGLISH} "Unable to check running copies of Files.exe. The operation has been stopped."
LangString RequiresX64 ${LANG_RUSSIAN} "Требуется 64-разрядная Windows."
LangString RequiresX64 ${LANG_ENGLISH} "64-bit Windows is required."
LangString UnexpectedPath ${LANG_RUSSIAN} "Неожиданная папка удаления. Операция отменена."
LangString UnexpectedPath ${LANG_ENGLISH} "Unexpected uninstall folder. The operation has been cancelled."
LangString RemoveData ${LANG_RUSSIAN} "Удалить также настройки и данные Consysto Files?$\r$\n$INSTDIR\data$\r$\n$\r$\nВыберите «Нет», чтобы сохранить их для следующей установки."
LangString RemoveData ${LANG_ENGLISH} "Also delete Consysto Files settings and data?$\r$\n$INSTDIR\data$\r$\n$\r$\nChoose No to keep them for a future installation."
LangString UninstallFailed ${LANG_RUSSIAN} "Некоторые файлы не удалось удалить. Закройте программы, использующие эту папку, и повторите удаление."
LangString UninstallFailed ${LANG_ENGLISH} "Some files could not be removed. Close applications using this folder and run the uninstaller again."

!macro CheckFiles PREFIX
Function ${PREFIX}CheckFiles
  System::Call 'kernel32::SetEnvironmentVariableW(w "CONSYSTO_SETUP_TARGET", w "$INSTDIR\Files.exe") i.r0'
  ${If} $0 == 0
    MessageBox MB_ICONSTOP "$(ProcessCheckFailed)" /SD IDOK
    SetErrorLevel 20
    Abort
  ${EndIf}
check:
  ; NSIS — x86; Sysnative даёт 64-битный PowerShell, который читает путь x64 Files.exe.
  nsExec::ExecToStack /TIMEOUT=30000 '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -EncodedCommand ${PROCESS_CHECK}'
  Pop $0
  Pop $1
  ${If} $0 == 10
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(CloseFiles)" /SD IDCANCEL IDRETRY check
    SetErrorLevel 10
    Abort
  ${ElseIf} $0 != 0
    MessageBox MB_ICONSTOP "$(ProcessCheckFailed)" /SD IDOK
    SetErrorLevel 20
    Abort
  ${EndIf}
FunctionEnd
!macroend
!insertmacro CheckFiles ""
!insertmacro CheckFiles "un."

Function .onInit
  SetShellVarContext current
  ; Фиксированный путь, включая тихий запуск с переданным /D=.
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\Consysto Files"
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(RequiresX64)" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Call CheckFiles
FunctionEnd

Section "$(AppSection)" AppSectionId
  SectionIn RO
  Call CheckFiles
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r /x data "${PAYLOAD}\*"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Consysto Files.lnk" "$INSTDIR\Files.exe" "" "$INSTDIR\Files.exe" 0
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Consysto Files"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Egor Chayka"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" '$\"$INSTDIR\Files.exe$\",0'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${ESTIMATED_SIZE}
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallerLanguage" "$LANGUAGE"
SectionEnd

Section "$(DesktopSection)" DesktopSectionId
  CreateShortcut "$DESKTOP\Consysto Files.lnk" "$INSTDIR\Files.exe" "" "$INSTDIR\Files.exe" 0
SectionEnd

Function un.onInit
  SetShellVarContext current
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallerLanguage"
  ${If} $0 != ""
    StrCpy $LANGUAGE $0
  ${EndIf}
  ${If} $INSTDIR != "$LOCALAPPDATA\Programs\Consysto Files"
    MessageBox MB_ICONSTOP "$(UnexpectedPath)" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Call un.CheckFiles
FunctionEnd

Section "Uninstall"
  Call un.CheckFiles
  StrCpy $3 0
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "$(RemoveData)" /SD IDNO IDNO keepData
    StrCpy $3 1
keepData:
  ; Убираем также файлы прежних версий; data пропускаем без явного согласия.
  StrCpy $4 0
  FindFirst $0 $1 "$INSTDIR\*"
loop:
  StrCmp $1 "" done
  StrCmp $1 "." next
  StrCmp $1 ".." next
  StrCmp $1 "Uninstall.exe" next
  ${If} $1 == "data"
  ${AndIf} $3 == 0
    Goto next
  ${EndIf}
  ClearErrors
  IfFileExists "$INSTDIR\$1\*" 0 file
    RMDir /r "$INSTDIR\$1"
    Goto checked
file:
    Delete "$INSTDIR\$1"
checked:
  ${If} ${Errors}
    StrCpy $4 1
  ${EndIf}
next:
  FindNext $0 $1
  Goto loop
done:
  FindClose $0
  ${If} $4 != 0
    MessageBox MB_ICONSTOP "$(UninstallFailed)" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$DESKTOP\Consysto Files.lnk"
  Delete "$SMPROGRAMS\Consysto Files.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
