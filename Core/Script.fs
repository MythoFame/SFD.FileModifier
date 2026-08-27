namespace SFD.FileModifier.Core

open System
open System.IO

/// Backend operations for Superfighters Deluxe extension script files (.sfde).
///
/// Extension scripts share the map header engine and additionally carry an embedded
/// C# source section (h_exscript) plus mirrored game-mode availability; loading
/// enforces both rules.
type SfdScript private (document: SfdDocument) =

    /// Loads and validates a .sfde file.
    static member Load(path: string) : SfdScript =
        let extension = Path.GetExtension(path).ToLowerInvariant()

        if extension <> ".sfde" then
            raise (SfdExtensionException(".sfde", extension))

        let document = SfdDocument.Load(path)

        if not document.MasterHeader.IsExtensionScript then
            raise (
                SfdFormatException
                    "'h_exscript' content missing from a .sfde file. This looks like a plain map stored under the wrong extension."
            )

        SfdScript document

    /// Snapshot of the master part header.
    member _.Info: SfdHeader = document.MasterHeader

    /// Number of component parts declared by the parts table (normally 1).
    member _.PartCount: int = document.PartCount

    // ------------------------------------------------------------------
    // Embedded script source
    // ------------------------------------------------------------------

    /// The embedded C# script source of the first part (h_exscript, raw UTF-8).
    member _.ScriptSource: string option = document.GetScriptSource(0)

    member _.GetScriptSourceAt(partIndex: int) : string option = document.GetScriptSource(partIndex)

    /// Replaces the embedded C# script source. Null characters are rejected.
    member this.SetScriptSource(source: string) : unit = document.SetScriptSource(0, source)

    /// Per-part variant for hypothetical multi-part extension scripts.
    member this.SetScriptSourceAt(partIndex: int, source: string) : unit =
        document.SetScriptSource(partIndex, source)

    /// The declared game modes of the extension script (h_ext / property ScriptTypes).
    member _.ScriptTypes: string option = document.MasterHeader.ScriptTypes

    // ------------------------------------------------------------------
    // Metadata
    // ------------------------------------------------------------------

    member _.MapCategory: SfdMapCategory = Header.mapTypeOf document.MasterHeader

    member this.SetMapCategory(category: SfdMapCategory) : unit = document.SetMapCategory category

    member _.MaxPlayers: int = document.MasterHeader.TotalPlayers

    member this.SetMaxPlayers(players: int) : unit = document.SetMaxPlayers players

    member _.Tags: SfdTag list = SfdTag.parseList document.MasterHeader.Tags

    member this.SetTags(tags: SfdTag list) : unit = document.SetTags tags

    member _.IsTemplate: bool = document.MasterHeader.IsTemplate

    member this.SetTemplate(isTemplate: bool) : unit = document.SetTemplate isTemplate

    /// Game modes the extension appears in ("Versus", "Custom", "Campaign", "Survival").
    member _.GameModes: string list = document.GetGameModes()

    /// Sets availability in both mirrors: every h_ext header section plus the
    /// ScriptTypes world property of every part.
    member this.SetGameModes(modes: string list) : unit = document.SetGameModes modes

    // ------------------------------------------------------------------
    // World settings
    // ------------------------------------------------------------------

    member _.CameraArea: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.CameraArea, 0)
        |> Option.bind (function
            | WpString s -> Some s
            | _ -> None)

    member _.SetCameraArea(cameraArea: string) : unit =
        Validation.cameraArea cameraArea
        document.SetWorldProperty(WorldPropertyIds.CameraArea, WpString cameraArea)

    member _.WorldBottom: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.Bottom, 0)
        |> Option.bind (function
            | WpString s -> Some s
            | _ -> None)

    member _.SetWorldBottom(bottom: string) : unit =
        Validation.floatText bottom "World bottom"
        document.SetWorldProperty(WorldPropertyIds.Bottom, WpString bottom)

    member _.Weather: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.Weather, 0)
        |> Option.bind (function
            | WpString s -> Some s
            | _ -> None)

    member _.SetWeather(weather: string) : unit =
        Validation.nullFreeText weather "Weather"
        document.SetWorldProperty(WorldPropertyIds.Weather, WpString weather)

    member _.StartCommands: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.StartCommands, 0)
        |> Option.bind (function
            | WpString s -> Some s
            | _ -> None)

    member _.SetStartCommands(commands: string) : unit =
        Validation.nullFreeText commands "Start commands"
        document.SetWorldProperty(WorldPropertyIds.StartCommands, WpString commands)

    member _.TryGetWorldProperty(propertyId: int) : WorldPropertyValue option =
        document.TryGetWorldPropertyOfPart(propertyId, 0)

    member _.SetWorldProperty(propertyId: int, value: WorldPropertyValue) : unit =
        document.SetWorldProperty(propertyId, value)

    // ------------------------------------------------------------------
    // Core lock + persistence operations
    // ------------------------------------------------------------------

    member this.UnlockOfficial() : unit = document.UnlockOfficial()

    member this.LockOfficial() : unit = document.LockOfficial()

    member this.SetAuthorLock(lockValue: bool) : unit = document.SetAuthorLock lockValue

    member this.SetPublishId(publishId: string) : unit = document.SetPublishId publishId

    member this.SetVersion(versionCode: string) : unit = document.SetVersion versionCode

    /// Writes the current state to an explicit output path.
    member _.Save(outputPath: string) : unit = document.Save outputPath
