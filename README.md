# OmegaDev2

Standalone WinUI 3 dev tools app for the 1.52 MHServerEmu fork.

Sibling to OmegaDev (which serves the 1.53 tree). OmegaDev2 talks to the
fork's server via its WebAPI. Nothing here touches client `.sip` files.

## Status

Bare shell + Home page + Teleport Pad placeholder.

## Build

```
cd src\OmegaDev2
dotnet build -c Release -r win-x64 --self-contained false
```
