# dotnet-purge

.NET tool that runs `dotnet clean` for each target framework and configuration and then deletes the intermediate and final output directories.

## Installation

```bash
dotnet tool install -g dotnet-purge
```

## Usage

Purge the project in the current directory:

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

Purge the project in the specified directory:

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
