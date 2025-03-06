# dotnet-purge

.NET tool that runs `dotnet clean` for each target framework and configuration and then deletes the intermediate and final output directories.
Can be run in a directory containing a solution or project file.

## Installation

```bash
dotnet tool install -g dotnet-purge
```

## Usage

Purge the solution/project in the current directory:

```bash
~/src/MyProject
$ dotnet purge
Running 'dotnet clean --configuration Debug --framework net8.0'... done!
Running 'dotnet clean --configuration Debug --framework net9.0'... done!
Running 'dotnet clean --configuration Release --framework net8.0'... done!
Running 'dotnet clean --configuration Release --framework net9.0'... done!
Deleting '/home/damian/src/MyProject/obj'... done!
Deleting '/home/damian/src/MyProject/bin'... done!
```

Purge the solution/project in the specified directory:

```bash
~/src
$ dotnet purge ./MyProject
Running 'dotnet clean --configuration Debug --framework net8.0'... done!
Running 'dotnet clean --configuration Debug --framework net9.0'... done!
Running 'dotnet clean --configuration Release --framework net8.0'... done!
Running 'dotnet clean --configuration Release --framework net9.0'... done!
Deleting '/home/damian/src/MyProject/obj'... done!
Deleting '/home/damian/src/MyProject/bin'... done!
```


## Add to Windows Explorer

Use [context-menu.reg](/context-menu.reg) to add dotnet-purge to the Windows Explorer context menu.

context-menu.reg contents:

```
Windows Registry Editor Version 5.00
[HKEY_CLASSES_ROOT\Directory\Shell]
@="none"
[HKEY_CLASSES_ROOT\Directory\shell\dotnet-purge]
"MUIVerb"="run dotnet-purge"
"Position"="bottom"
[HKEY_CLASSES_ROOT\Directory\Background\shell\dotnet-purge]
"MUIVerb"="run dotnet-purge"
"Position"="bottom"
[HKEY_CLASSES_ROOT\Directory\shell\dotnet-purge\command]
@="cmd.exe /c cd \"%V\" & dotnet-purge"
[HKEY_CLASSES_ROOT\Directory\Background\shell\dotnet-purge\command]
@="cmd.exe /c cd \"%V\" & dotnet-purge"
```