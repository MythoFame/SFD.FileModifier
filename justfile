publish:
    dotnet publish src/SFD.FileModifier.TUI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
    dotnet publish src/SFD.FileModifier.TUI -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
