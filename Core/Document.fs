namespace SFD.FileModifier.Core

open System

/// Body information of a single map part: the leading c_wp section when present
/// plus the embedded c_scrpt script of regular maps.
type internal PartBody =
    { WorldProperties: WorldProperties option
      /// Decoded C# source from c_scrpt together with the splicable range of the
      /// length-prefixed Base64 string. Only present in regular maps.
      MapScriptSource: (string * ByteRange) option }

module internal PartBody =

    let empty = { WorldProperties = None; MapScriptSource = None }

/// c_scrpt payload helpers: the game stores Base64(UTF-8 source) as one
/// length-prefixed string.
module internal Decode =

    /// Decodes a c_scrpt value into its inner C# source, falling back to an empty
    /// script when the stored bytes are not valid Base64 (older writers).
    let mapScript (rawBase64: string) : string =
        try
            Convert.FromBase64String rawBase64 |> Text.Encoding.UTF8.GetString
        with :? FormatException -> ""

/// One parsed component world of a file. Part 1 is the master part and is the only
/// one carrying the h_pt parts table and thumbnail; later campaign parts mirror the
/// shared metadata inside their own headers.
type internal SfdPart =
    { Index: int
      StartPosition: int
      Header: SfdHeader
      Body: PartBody }

[<RequireQualifiedAccess>]
module internal PartParsing =

    let parseHeaderAt (data: byte[]) (startPosition: int) : SfdHeader =
        if startPosition <= 0 || startPosition >= data.Length then
            raise (
                SfdFormatException $"Part start position {startPosition} lies outside the file ({data.Length} bytes); the parts table appears to be corrupt."
            )

        Header.parse data startPosition

    let parseBody (data: byte[]) (headerEnd: int) : PartBody =
        if headerEnd < 0 || headerEnd >= data.Length then
            PartBody.empty
        else
            try
                let reader = SfdBinaryReader(data, headerEnd)
                let mutable worldProperties = None
                let mutable mapScript = None

                // The game save order guarantees at most two leading body sections of
                // interest here: c_wp, followed optionally by c_scrpt in regular maps.
                let firstToken = reader.ReadStringNonNull()

                if firstToken = Tokens.WorldPropertiesSection then
                    worldProperties <- Some(WorldProperties.parseSection reader)

                    if reader.Remaining > 0 then
                        let secondToken = reader.ReadStringNonNull()

                        if secondToken = Tokens.MapScriptSection then
                            let raw = reader.ReadString()
                            mapScript <- Some(Decode.mapScript raw, reader.LastRange)

                elif firstToken = Tokens.MapScriptSection then
                    let raw = reader.ReadString()
                    mapScript <- Some(Decode.mapScript raw, reader.LastRange)

                { WorldProperties = worldProperties; MapScriptSource = mapScript }

            with
            | :? SfdException -> PartBody.empty

    /// Parses every component world declared by the master parts table.
    /// The file always begins with the master part; its stored position is pinned to 0.
    let parseParts (data: byte[]) : SfdPart list =
        let masterHeader = Header.parse data 0

        let masterPart =
            { Index = 0
              StartPosition = 0
              Header = masterHeader
              Body = parseBody data masterHeader.HeaderEnd }

        let laterParts =
            masterHeader.Parts
            |> List.skip 1
            |> List.mapi (fun offset (partEntry: SfdMapPart) ->
                let index = offset + 1
                let header = parseHeaderAt data partEntry.StartPosition

                { Index = index
                  StartPosition = partEntry.StartPosition
                  Header = header
                  Body = parseBody data header.HeaderEnd })

        masterPart :: laterParts

/// Shared editing engine behind the SfdMap / SfdScript facades.
///
/// Superfighters Deluxe files are streams of tagged sections. Everything an operation
/// touches has been located during parsing; mutations are performed as surgical
/// byte-range splices applied in one atomic commit followed by a full re-parse.
type SfdDocument private (initialBytes: byte[]) =

    let splice (source: byte[]) (range: ByteRange) (replacement: byte[]) : byte[] =
        if range.Start < 0 || range.End > source.Length || range.Start > range.End then
            raise (
                SfdException $"Invalid byte range [{range.Start}, {range.End}) against a file of {source.Length} bytes."
            )

        let head =
            if range.Start = 0 then [||] else Array.sub source 0 range.Start

        let tail =
            if range.End >= source.Length then [||] else Array.sub source range.End (source.Length - range.End)

        Array.concat [ head; replacement; tail ]

    let mutable data = initialBytes
    let mutable parts = PartParsing.parseParts initialBytes

    member _.Data: byte[] = Array.copy data

    member _.Length = data.Length

    /// The master part; its header drives metadata views for the whole file.
    member internal _.MasterPart: SfdPart = List.head parts

    member this.MasterHeader: SfdHeader = this.MasterPart.Header

    member internal _.Parts: SfdPart list = parts

    member _.PartCount = List.length parts

    /// Loads a map or script document from disk.
    static member Load(path: string) : SfdDocument = SfdDocument(SfdIo.readAllBytes path)

    /// Wraps already loaded bytes.
    static member FromBytes(bytes: byte[]) : SfdDocument = SfdDocument(Array.copy bytes)

    // ------------------------------------------------------------------
    // Commit machinery
    // ------------------------------------------------------------------

    /// Rebuilds the h_pt payload while applying an optional chapter rename and shifting
    /// every non-master part start position by the accumulated deltas of content edits
    /// preceding it plus the table's own size change.
    ///
    /// Table payload length depends solely on names (integers keep their fixed width),
    /// so the position math stays independent of the resulting serialized bytes.
    member _.BuildPatchedTable
        (
            masterParts: SfdMapPart list,
            contentEdits: (ByteRange * byte[]) list,
            rename: (int * string) option
        ) : byte[] =

        match rename with
        | Some(index, newName) ->
            if index < 0 || index >= masterParts.Length then
                raise (SfdValidationException $"Chapter index {index} is out of range (0..{masterParts.Length - 1}).")

            if isNull newName then
                raise (SfdValidationException "Chapter name cannot be null.")
            elif newName.Contains('\u0000') then
                raise (SfdValidationException "Chapter name cannot contain null characters.")
        | None -> ()

        let renamedEntries =
            match rename with
            | Some(index, newName) ->
                masterParts
                |> List.mapi (fun i p -> if i = index then { p with Name = newName } else p)
            | None -> masterParts

        let originalLength = Header.serializePartsTable masterParts |> Array.length
        let renamedLength = Header.serializePartsTable renamedEntries |> Array.length
        let ownDelta = renamedLength - originalLength

        let shiftedEntries =
            renamedEntries
            |> List.mapi (fun i entry ->
                if i = 0 then entry
                else
                    let shiftFromContent =
                        contentEdits
                        |> List.sumBy (fun (range: ByteRange, replacement) ->
                            if range.Start < entry.StartPosition then replacement.Length - range.Length else 0)

                    { entry with StartPosition = entry.StartPosition + ownDelta + shiftFromContent })

        Header.serializePartsTable shiftedEntries

    /// Atomically applies content edits. When any edit changes sizes on a multi-part
    /// file, the h_pt start positions are recomputed and rewritten inside the same
    /// batch, because the game loads each later part through its stored offset.
    /// Chapter renames travel through the optional parameter so names may change size.
    member private this.CommitCore(contentEdits: (ByteRange * byte[]) list, rename: (int * string) option) : unit =
        let masterHeader = this.MasterPart.Header
        let isMultiPart = masterHeader.Parts.Length > 1

        let sortedDesc =
            contentEdits
            |> List.sortByDescending (fun (r: ByteRange, _) -> r.Start)

        let allEdits =
            match rename, masterHeader.PartsTableRange with
            | Some _, None ->
                raise (
                    SfdFormatException "Renaming a chapter requires an h_pt parts table, which this file does not contain."
                )
            | _, None -> sortedDesc
            | None, Some tableRange ->
                // Patch positions only when some edit actually changes sizes;
                // otherwise leave the table untouched so commits stay byte-preserving.
                let anyDelta =
                    sortedDesc |> List.exists (fun (r, replacement) -> replacement.Length <> r.Length)

                if isMultiPart && anyDelta then
                    (tableRange, this.BuildPatchedTable (masterHeader.Parts, sortedDesc, None)) :: sortedDesc
                else
                    sortedDesc
            | Some(index, newName), Some tableRange ->
                (tableRange, this.BuildPatchedTable (masterHeader.Parts, sortedDesc, Some(index, newName)))
                :: sortedDesc

        // Final overlap validation includes any synthetic table edit from above.
        let ordered =
            List.sortByDescending (fun (r: ByteRange, _) -> r.Start) allEdits

        let mutable ceiling = Int32.MaxValue

        for range, _ in ordered do
            if range.End > ceiling then
                raise (
                    SfdException $"Refusing overlapping edits: [{range.Start}, {range.End}) crosses a previous edit boundary."
                )

            ceiling <- range.Start

        let updated =
            ordered |> List.fold (fun acc (range, replacement) -> splice acc range replacement) data

        data <- updated
        parts <- PartParsing.parseParts updated

    /// Applies non-overlapping edits atomically without structural bookkeeping.
    member this.Apply(edits: (ByteRange * byte[]) list) : unit = this.CommitCore(edits, None)

    /// Renames one chapter of the parts table; start positions refresh through the
    /// structural path even though no other content changes.
    member this.RenameChapter(chapterIndex: int, newName: string) : unit =
        this.CommitCore([], Some(chapterIndex, newName))

    // ------------------------------------------------------------------
    // World property access
    // ------------------------------------------------------------------

    /// Returns the world property value stored in the given part, when present.
    member _.TryGetWorldPropertyOfPart(propertyId: int, partIndex: int) : WorldPropertyValue option =
        parts
        |> List.tryFind (fun p -> p.Index = partIndex)
        |> Option.bind (fun p -> p.Body.WorldProperties)
        |> Option.bind (WorldProperties.tryFind propertyId)
        |> Option.map (fun entry -> entry.Value)

    /// Returns the world property values across every part that stores them,
    /// ordered by part index.
    member _.TryGetWorldPropertyEverywhere(propertyId: int) : WorldPropertyValue list =
        parts
        |> List.choose (fun part ->
            part.Body.WorldProperties
            |> Option.bind (WorldProperties.tryFind propertyId)
            |> Option.map (fun entry -> entry.Value))

    /// Edits required to set one world property on one part. Values are replaced
    /// key+type+value wholesale so even kind changes stay valid; missing properties
    /// are appended after the last stored entry with a bumped count field.
    member internal _.WorldPropertyEditsForPart (part: SfdPart) (propertyId: int) (value: WorldPropertyValue) :
        (ByteRange * byte[]) list =
        match part.Body.WorldProperties with
        | None -> []
        | Some properties ->
            match WorldProperties.tryFind propertyId properties with
            | Some existing ->
                let tripleStart = existing.ValueRange.Start - 8

                [ ByteRange.create tripleStart existing.ValueRange.End,
                  WorldProperties.serializeEntry propertyId value ]
            | None ->
                [
                    properties.CountFieldRange, BitConverter.GetBytes(properties.Properties.Length + 1)
                    ByteRange.create properties.SectionEnd properties.SectionEnd,
                    WorldProperties.serializeEntry propertyId value
                ]

    /// Sets one world property in every part; appends it where it does not exist yet.
    member this.SetWorldProperty(propertyId: int, value: WorldPropertyValue) : unit =
        let edits =
            parts
            |> List.collect (fun part -> this.WorldPropertyEditsForPart part propertyId value)

        if List.isEmpty edits then
            raise (
                SfdFormatException
                    "No component part contains a 'c_wp' world properties section, which is required for this operation."
            )
        else
            this.Apply edits

    // ------------------------------------------------------------------
    // Shared operations
    // ------------------------------------------------------------------

    /// Replaces the version string stored in h_gv of every part.
    member this.SetVersion(versionCode: string) : unit =
        Validation.versionCode versionCode
        let encoded = SfdEncode.string versionCode

        this.Apply (
            parts
            |> List.map (fun part ->
                match part.Header.VersionRange with
                | Some range -> range, encoded
                | None -> raise (SfdHeaderNotFoundException Tokens.GuidVersion))
        )

    /// Replaces the publish ID everywhere it lives: each part's h_pei header value and
    /// each part's world property Map_PublishExternalID (333).
    member this.SetPublishId(publishId: string) : unit =
        Validation.publishId publishId
        let encoded = SfdEncode.string publishId

        let edits =
            parts
            |> List.collect (fun part ->
                let headerEdit =
                    match part.Header.PublishExternalIdRange with
                    | Some range -> range, encoded
                    | None -> raise (SfdHeaderNotFoundException Tokens.PublishExternalId)

                match this.WorldPropertyEditsForPart part WorldPropertyIds.PublishExternalId (WpString publishId) with
                | [] ->
                    raise (
                        SfdFormatException
                            "A component part does not contain a 'c_wp' world properties section, which is required to set the publish ID."
                    )
                | propertyEdits -> headerEdit :: propertyEdits)

        this.Apply edits

    /// Sets or clears the author/editor edit lock in every part (h_el + property 332).
    member this.SetAuthorLock(lockValue: bool) : unit =
        let encoded = SfdEncode.bool lockValue

        this.Apply (
            parts
            |> List.collect (fun part ->
                let headerEdit =
                    match part.Header.EditLockByteRange with
                    | Some range -> [ range, encoded ]
                    | None -> raise (SfdHeaderNotFoundException Tokens.EditLock)

                let propertyEdits =
                    this.WorldPropertyEditsForPart part WorldPropertyIds.EditLock (WpBool lockValue)

                headerEdit @ propertyEdits)
        )

    /// Rewrites the h_mt marker of every part so the game treats the file as editable.
    member this.UnlockOfficial() : unit =
        let encoded = SfdEncode.utf8 Tokens.EditorMarkerValue

        this.Apply (
            parts
            |> List.map (fun part ->
                match part.Header.OfficialMarkerRange with
                | Some range -> range, encoded
                | None -> raise (SfdHeaderNotFoundException Tokens.EditorMarker))
        )

    /// Computes the official lock token per part from its own name and author and
    /// stores it in h_mt.
    member this.LockOfficial() : unit =
        this.Apply (
            parts
            |> List.map (fun part ->
                match part.Header.OfficialMarkerRange with
                | Some range -> range, OfficialToken.computeBytes part.Header.Name part.Header.Author
                | None -> raise (SfdHeaderNotFoundException Tokens.EditorMarker))
        )

    /// Rewrites the h_mtp pair of every part while preserving each sibling value.
    member private this.SetMapTypePlayers(typeOf: int option, playersOf: int option) : unit =
        this.Apply (
            parts
            |> List.map (fun part ->
                match part.Header.MapTypePlayersRange with
                | None -> raise (SfdHeaderNotFoundException Tokens.MapTypePlayers)
                | Some range ->
                    let typeValue = typeOf |> Option.defaultValue part.Header.MapType
                    let playerValue = playersOf |> Option.defaultValue part.Header.TotalPlayers

                    range,
                    Array.append
                        (BitConverter.GetBytes typeValue)
                        (BitConverter.GetBytes playerValue))
        )

    /// Changes the declared map category of every part (h_mtp + property 103).
    member this.SetMapCategory(category: SfdMapCategory) : unit =
        match category with
        | UnknownCategory raw ->
            raise (
                SfdValidationException
                    $"Unknown map category value {raw}; expected Versus, Custom, Campaign, Survival or Challenge."
            )
        | _ -> ()

        let rawType = SfdMapCategory.toInt category
        this.SetMapTypePlayers(Some rawType, None)
        this.SetWorldProperty(WorldPropertyIds.MapType, WpInt rawType)

    /// Changes the declared maximum player count of every part (h_mtp + property 262).
    member this.SetMaxPlayers(players: int) : unit =
        if players < 0 || players > 16 then
            raise (SfdValidationException $"Maximum player count must be between 0 and 16; got {players}.")

        this.SetMapTypePlayers(None, Some players)
        this.SetWorldProperty(WorldPropertyIds.TotalPlayers, WpInt players)

    /// Changes the custom tags of every part (h_tg + property 337).
    member this.SetTags(tags: SfdTag list) : unit =
        SfdTag.validate tags
        let formatted = tags |> Seq.map (SfdTag.idOf >> string) |> String.concat ","
        let encoded = SfdEncode.string formatted

        this.Apply (
            parts
            |> List.collect (fun part ->
                let headerEdit =
                    match part.Header.TagsRange with
                    | Some range -> [ range, encoded ]
                    | None -> raise (SfdHeaderNotFoundException Tokens.Tags)

                let propertyEdits =
                    this.WorldPropertyEditsForPart part WorldPropertyIds.Tags (WpString formatted)

                headerEdit @ propertyEdits)
        )

    /// Toggles the template flag of every part (h_tmp + property 331).
    member this.SetTemplate(isTemplate: bool) : unit =
        let byteEncoded = SfdEncode.bool isTemplate

        this.Apply (
            parts
            |> List.collect (fun part ->
                let headerEdit =
                    match part.Header.TemplateByteRange with
                    | Some range -> [ range, byteEncoded ]
                    | None -> raise (SfdHeaderNotFoundException Tokens.Template)

                let propertyEdits =
                    this.WorldPropertyEditsForPart part WorldPropertyIds.IsTemplate (WpBool isTemplate)

                headerEdit @ propertyEdits)
        )

    // ------------------------------------------------------------------
    // Game mode availability
    // ------------------------------------------------------------------

    /// Game mode availability: maps store it solely in the ScriptTypes world property
    /// (339); extension scripts also mirror it in their h_ext header sections.
    member this.GetGameModes() : string list =
        let master = this.MasterPart

        let modesFromHeader =
            master.Header.ScriptTypes
            |> Option.toList
            |> List.collect SfdGameModes.parse

        let modesFromProperty =
            master.Body.WorldProperties
            |> Option.bind (WorldProperties.tryFind WorldPropertyIds.ScriptTypes)
            |> Option.map (fun entry -> entry.Value)
            |> Option.bind (function WpString s -> Some s | _ -> None)
            |> Option.toList
            |> List.collect SfdGameModes.parse

        if not (List.isEmpty modesFromHeader) then modesFromHeader
        elif not (List.isEmpty modesFromProperty) then modesFromProperty
        else []

    /// Sets the game mode availability string in every part's property store and in
    /// every extension-script h_ext header section.
    member this.SetGameModes(modes: string list) : unit =
        SfdGameModes.validate modes
        let formatted = SfdGameModes.format modes
        let encoded = SfdEncode.string formatted

        let edits =
            parts
            |> List.collect (fun part ->
                let headerEdits =
                    if part.Header.IsExtensionScript then
                        match part.Header.ExtensionScriptTypesRange with
                        | Some range -> [ range, encoded ]
                        | None -> []
                    else []

                let propertyEdits =
                    this.WorldPropertyEditsForPart part WorldPropertyIds.ScriptTypes (WpString formatted)

                headerEdits @ propertyEdits)

        if List.isEmpty edits then
            raise (
                SfdFormatException
                    "The file contains neither an h_ext header nor a ScriptTypes world property to store game modes in."
            )
        else
            this.Apply edits

    // ------------------------------------------------------------------
    // Embedded scripts
    // ------------------------------------------------------------------

    /// Returns the decoded inner script of the given part.
    /// Maps store it Base64-encoded in c_scrpt; extension scripts carry raw
    /// null-delimited source in their h_exscript sections.
    member this.GetScriptSource(partIndex: int) : string option =
        match parts |> List.tryFind (fun p -> p.Index = partIndex) with
        | None -> raise (SfdValidationException $"Part index {partIndex} does not exist in this file.")
        | Some part ->
            if part.Header.IsExtensionScript then part.Header.ScriptSource
            else part.Body.MapScriptSource |> Option.map fst

    /// Rewrites the inner script of the given part.
    /// Map payloads are re-encoded as canonical Base64 exactly like the game writer;
    /// extension script payloads stay null-delimited and reject null characters.
    /// Length-changing script edits on multi-part files automatically refresh the
    /// h_pt start positions through Commit.
    member this.SetScriptSource(partIndex: int, source: string) : unit =
        match parts |> List.tryFind (fun p -> p.Index = partIndex) with
        | None -> raise (SfdValidationException $"Part index {partIndex} does not exist in this file.")
        | Some part ->
            if part.Header.IsExtensionScript then
                if source <> null && source.Contains('\u0000') then
                    raise (SfdValidationException "Script source cannot contain null characters.")

                match part.Header.ScriptSourceRange with
                | Some range -> this.Apply [ range, SfdEncode.nullDelimitedString source ]
                | None -> raise (SfdHeaderNotFoundException Tokens.ExtensionScriptSource)
            else
                match part.Body.MapScriptSource with
                | None ->
                    raise (SfdFormatException $"Part {partIndex} does not contain a '{Tokens.MapScriptSection}' section.")
                | Some(_, range) ->
                    let base64Text = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes source)
                    this.Apply [ range, SfdEncode.string base64Text ]

    // ------------------------------------------------------------------
    // Chapters
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    /// Writes the current state to an explicit output path.
    member this.Save(outputPath: string) : unit =
        if String.IsNullOrWhiteSpace outputPath then
            raise (SfdValidationException "Output path cannot be empty.")

        try
            IO.File.WriteAllBytes(outputPath, data)
        with
        | :? UnauthorizedAccessException -> raise (SfdException $"Access denied writing to '{outputPath}'.")
        | :? System.IO.IOException as ex -> raise (SfdException $"Failed writing '{outputPath}': {ex.Message}")
        | :? System.ArgumentException -> raise (SfdException $"'{outputPath}' is not a valid output path.")
