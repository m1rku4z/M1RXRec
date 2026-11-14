[Setup]
AppName=M1RXRec
AppVersion=1.0
DefaultDirName={autopf}\M1RXRec
DefaultGroupName=M1RXRec
OutputBaseFilename=M1RXRec-Setup
Compression=lzma2
PrivilegesRequired=lowest
[Files]
Source: "out\M1RXRec.exe"; DestDir: "{app}"
Source: "out\ffmpeg.exe"; DestDir: "{app}"
[Icons]
Name: "{autodesktop}\M1RXRec"; Filename: "{app}\M1RXRec.exe"
Name: "{group}\M1RXRec"; Filename: "{app}\M1RXRec.exe"
[Run]
Filename: "{app}\M1RXRec.exe"; Flags: postinstall nowait
