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
