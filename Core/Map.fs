namespace SFD.FileModifier.Core

open System
open System.IO

/// Backend operations for Superfighters Deluxe map files (.sfdm).
///
/// Map files are parsed with the shared SFD format engine and must not contain
/// extension-script sections; loading enforces both rules. Campaign maps consist of
/// several component parts (chapters); every metadata operation fans out to all of
/// them and keeps the parts table positions correct.
type SfdMap private (document: SfdDocument) =

    /// Loads and validates a .sfdm file.
    static member Load(path: string) : SfdMap =
        let extension = Path.GetExtension(path).ToLowerInvariant()

        if extension <> ".sfdm" then
            raise (SfdExtensionException(".sfdm", extension))

        let document = SfdDocument.Load(path)

        if document.MasterHeader.IsExtensionScript then
            raise (
                SfdFormatException
                    "'h_exscript' content found inside a .sfdm file. This looks like an extension script stored under the wrong extension."
            )

        SfdMap document

    /// Snapshot of the master part header; drives metadata views for the whole file.
    member _.Info: SfdHeader = document.MasterHeader

    /// The chapters declared by the parts table.
    member _.Chapters: SfdMapPart list = document.MasterHeader.Parts

    /// Number of component parts (1 for regular maps, more inside campaigns).
    member _.PartCount: int = document.PartCount

    // ------------------------------------------------------------------
    // Metadata
    // ------------------------------------------------------------------

    member _.MapCategory: SfdMapCategory = Header.mapTypeOf document.MasterHeader

    member this.SetMapCategory(category: SfdMapCategory) : unit = document.SetMapCategory category

    member _.MaxPlayers: int = document.MasterHeader.TotalPlayers

    member this.SetMaxPlayers(players: int) : unit = document.SetMaxPlayers players

    /// Tag ids parsed from h_tg / property Tags, tolerated to contain unknown values.
    member _.Tags: SfdTag list = SfdTag.parseList document.MasterHeader.Tags

    member this.SetTags(tags: SfdTag list) : unit = document.SetTags tags

    member _.IsTemplate: bool = document.MasterHeader.IsTemplate

    member this.SetTemplate(isTemplate: bool) : unit = document.SetTemplate isTemplate

    /// Game modes the map appears in ("Versus", "Custom", "Campaign", "Survival").
    member _.GameModes: string list = document.GetGameModes()

    member this.SetGameModes(modes: string list) : unit = document.SetGameModes modes

    // ------------------------------------------------------------------
    // Chapters
    // ------------------------------------------------------------------

    /// Renames one chapter entry of the parts table. Part start positions shift so
    /// the game keeps loading every later chapter from its true offset.
    member this.RenameChapter(chapterIndex: int, newName: string) : unit =
        document.RenameChapter(chapterIndex, newName)

    // ------------------------------------------------------------------
    // Embedded scripts (one per chapter, Base64 encoded c_scrpt)
    // ------------------------------------------------------------------

    /// Inner script source of the first part.
    member _.ScriptSource: string option = document.GetScriptSource(0)

    /// Inner script sources of every part, in order; empty string when a part has none.
    member _.ScriptSources: string[] =
        [| for index in 0 .. document.PartCount - 1 do
               match document.GetScriptSource(index) with
               | Some source -> yield source
               | None -> yield "" |]

    member _.GetScriptSourceAt(partIndex: int) : string option = document.GetScriptSource(partIndex)

    member _.SetScriptSource(source: string) : unit = document.SetScriptSource(0, source)

    member _.SetScriptSourceAt(partIndex: int, source: string) : unit =
        document.SetScriptSource(partIndex, source)

    // ------------------------------------------------------------------
    // World settings
    // ------------------------------------------------------------------

    /// Camera bounds string like "240,-320,-240,320".
    member _.CameraArea: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.CameraArea, 0)
        |> Option.bind (function WpString s -> Some s | _ -> None)

    member _.SetCameraArea(cameraArea: string) : unit =
        Validation.cameraArea cameraArea
        document.SetWorldProperty(WorldPropertyIds.CameraArea, WpString cameraArea)

    /// World bottom boundary string, e.g. "-250".
    member _.WorldBottom: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.Bottom, 0)
        |> Option.bind (function WpString s -> Some s | _ -> None)

    member _.SetWorldBottom(bottom: string) : unit =
        Validation.floatText bottom "World bottom"
        document.SetWorldProperty(WorldPropertyIds.Bottom, WpString bottom)

    member _.Weather: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.Weather, 0)
        |> Option.bind (function WpString s -> Some s | _ -> None)

    member _.SetWeather(weather: string) : unit =
        Validation.nullFreeText weather "Weather"
        document.SetWorldProperty(WorldPropertyIds.Weather, WpString weather)

    /// Chat commands executed when the map starts.
    member _.StartCommands: string option =
        document.TryGetWorldPropertyOfPart(WorldPropertyIds.StartCommands, 0)
        |> Option.bind (function WpString s -> Some s | _ -> None)

    member _.SetStartCommands(commands: string) : unit =
        Validation.nullFreeText commands "Start commands"
        document.SetWorldProperty(WorldPropertyIds.StartCommands, WpString commands)

    /// Reads any world property straight from the first part's store.
    member _.TryGetWorldProperty(propertyId: int) : WorldPropertyValue option =
        document.TryGetWorldPropertyOfPart(propertyId, 0)

    /// Writes any world property into every part's store.
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
