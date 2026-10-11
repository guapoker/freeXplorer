# freeXplorer

A free Windows tool for Xbox 360 hard drives. It shows the partitions of a 360 drive as normal drives in File Explorer, formats the partitions a 360 drive is made of, and backs up or clones whole drives. It comes as a window and as a command line tool.

It was written from public documentation of the file system. No code from any other tool is in it.

## Heads up

freeXplorer writes to disks. Formatting and restoring a backup erase whatever is on the target, so check the disk number and keep a backup of anything you care about. Reading and mounting read-only have been tried on real drives. Backing up, writing, formatting and restoring have only been run on image files and on a virtual disk so far (a backup of a real drive was started and stopped again), and I haven't tried a drive made with it in a console yet.

## What you need

- Windows 10 or 11, 64-bit
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (the self-contained download already has it)
- [WinFsp](https://winfsp.dev), only for mounting. The Install driver button in the window downloads and installs it, or run `winget install WinFsp.WinFsp`
- Administrator rights to open a real drive. The window asks for them when it starts

## Download

Get a zip from the [releases page](https://github.com/AlvaroEPena/freeXplorer/releases), unzip it and run `freeXplorer.exe`.

- `freeXplorer-1.0.0-win-x64.zip` is tiny (under 1 MB) and needs the .NET runtime above
- `freeXplorer-1.0.0-win-x64-selfcontained.zip` carries the runtime with it, about 60 MB

## Using the window

1. Pick a drive on the left. Its partitions show up on the right.
2. Select a partition and press Mount. It appears in This PC as a new drive and its row turns green. It's read-only unless you tick Allow writing. Unmount before you unplug the drive.
3. Create backup saves the whole drive to one file and puts a `.sha256` file next to it. Load backup opens a backup like a drive so you can look inside. Restore backup writes a backup onto another drive to make it an exact clone.
4. Format erases the partitions you tick, or only the Xbox 1 Compatibility partition. You have to type the disk number first.
5. The switch at the top right turns dark mode on and off.

A backup is a raw copy of every sector, the same kind of image `dd` makes. On an NTFS disk the empty parts aren't stored, so the file is often much smaller than the drive. A restore needs a target at least as big as the original. A clone also gets the original drive's security sector, unless you tick Keep this drive's own security sector, which only works between drives of the same size.

## Command line

`freexplorer-cli.exe` does the same things without a window and a bit more. A target is `disk:N` (see `list`) or the path of an image file, and the partitions are called `content`, `compat`, `sysext` and `sysext2`.

```
freexplorer-cli list
freexplorer-cli info disk:3
freexplorer-cli ls disk:3 content \Content -l
freexplorer-cli get disk:3 content \Content\0000000000000000 C:\backup
freexplorer-cli put practice.img content C:\games\MyGame \Content\0000000000000000\4D5307E6
freexplorer-cli fsck disk:3 content
freexplorer-cli format practice.img --all --dry-run
freexplorer-cli format disk:3 --partitions compat
freexplorer-cli new-image practice.img --size 8G
freexplorer-cli backup disk:3 C:\backups\hdd.img
freexplorer-cli restore C:\backups\hdd.img disk:5
freexplorer-cli restore C:\backups\hdd.img disk:5 --keep-header
freexplorer-cli mount disk:3 content
freexplorer-cli mount practice.img content --rw --letter X: --hold 600
```

`freexplorer-cli help` lists every command. Real disks need an administrator prompt, and anything that erases a disk asks you to type `disk N` first (or pass `--confirm "disk N"` in a script). `--dry-run` shows what a format would write and writes nothing. Image files need no special rights, so they're good for practice.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
git clone https://github.com/AlvaroEPena/freeXplorer.git
cd freeXplorer
dotnet build
dotnet test
```

`.\build.ps1` runs the tests and puts both programs and a zip in `dist`. `.\build.ps1 -SelfContained` bundles the runtime too.

## Built with

C# on .NET 10, WPF for the window, [WinFsp](https://github.com/winfsp/winfsp) through the `winfsp.net` package for mounting, and xUnit for the tests.

- `src/FreeXplorer.Core` has the block devices, the FATX volume, the formatter and the Xbox 360 drive layout. It has no dependencies.
- `src/FreeXplorer.WinFsp` connects a volume to WinFsp and installs the driver.
- `src/FreeXplorer.Cli` is the command line tool.
- `src/FreeXplorer.App` is the window.
- `tests/FreeXplorer.Tests` has the tests.

## Sources

- Free60 wiki, [FATX](https://free60.org/System-Software/Systems/FATX/) and [the Xbox 360 hard drive](https://free60.org/Hardware/Console/HDD/), for the file system layout and the partition offsets
- [patatetom/xtaf](https://github.com/patatetom/xtaf), whose example output from a real drive was used to check the size calculations
- [Coding FATX support for 2.6](https://lkml.iu.edu/hypermail/linux/kernel/0709.2/3089.html) on the Linux kernel mailing list, for the general shape of the format
- [WinFsp](https://winfsp.dev) documentation

## Not affiliated

freeXplorer has nothing to do with FATXplorer or its author, or with Microsoft. Xbox and Xbox 360 are trademarks of Microsoft.

## License

MIT, see [LICENSE](LICENSE).

Mounting is powered by WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp. It's GPLv3 with an exception for open source software, see [THIRD-PARTY.txt](THIRD-PARTY.txt).
