namespace SFD.FileModifier.Core

open System

/// One entry of the h_pt parts table. Campaign chapters are named entries;
/// regular maps and extension scripts carry a single unnamed entry.
type SfdMapPart =
    { Name: string
      Selectable: bool
      StartPosition: int }

/// Sequentially parsed h_* header sections of a Superfighters Deluxe file,
/// together with the byte ranges required for surgical edits.
type SfdHeader =
    {
        Guid: Guid
        Version: string option
        OriginalGuid: Guid option
        OwnerHash: string option
        IsTemplate: bool
        EditLock: bool
        Name: string
        Author: string
        MapType: int
        TotalPlayers: int
        Tags: string
        Description: string
        SaveDate: DateTime option
        PublishExternalId: string
        /// Raw contents of h_mt: either Tokens.EditorMarkerValue or an official lock token.
        OfficialMarker: char[] option
        IsOfficial: bool
        IsExtensionScript: bool
        ScriptTypes: string option
        /// Splicable range of the h_ext value (extension scripts only).
        ExtensionScriptTypesRange: ByteRange option
        ScriptSource: string option
        HasThumbnail: bool
        /// Entries of h_pt; empty when the section is missing.
        Parts: SfdMapPart list
        VersionRange: ByteRange option
        PublishExternalIdRange: ByteRange option
        EditLockByteRange: ByteRange option
        OfficialMarkerRange: ByteRange option
        ScriptSourceRange: ByteRange option
        MapTypePlayersRange: ByteRange option
        TagsRange: ByteRange option
        TemplateByteRange: ByteRange option
        /// Full payload of h_pt (count field + all entries).
        PartsTableRange: ByteRange option
        /// Offset of the first body token (first non-h section).
        HeaderEnd: int
    }

[<RequireQualifiedAccess>]
module Header =

    let empty =
        { Guid = Guid.Empty
          Version = None
          OriginalGuid = None
          OwnerHash = None
          IsTemplate = false
          EditLock = false
          Name = ""
          Author = ""
          MapType = 0
          TotalPlayers = 0
          Tags = ""
          Description = ""
          SaveDate = None
          PublishExternalId = ""
          OfficialMarker = None
          IsOfficial = false
          IsExtensionScript = false
          ScriptTypes = None
          ExtensionScriptTypesRange = None
          ScriptSource = None
          HasThumbnail = false
          Parts = []
          VersionRange = None
          PublishExternalIdRange = None
          EditLockByteRange = None
          OfficialMarkerRange = None
          ScriptSourceRange = None
          MapTypePlayersRange = None
          TagsRange = None
          TemplateByteRange = None
          PartsTableRange = None
          HeaderEnd = 0 }

    let mapTypeOf (header: SfdHeader) : SfdMapCategory = SfdMapCategory.ofRaw header.MapType

    /// Serialises a parts table payload exactly like the game's writer:
    /// count + { length-prefixed name, boolean selectable, absolute start position }.
    let serializePartsTable (parts: SfdMapPart list) : byte[] =
        let entries =
            parts
            |> Seq.collect (fun part ->
                seq {
                    yield SfdEncode.string part.Name
                    yield SfdEncode.bool part.Selectable
                    yield BitConverter.GetBytes(part.StartPosition)
                })
            |> Array.concat

        Array.append (BitConverter.GetBytes(parts.Length)) entries

    /// Parses all header sections sequentially, mirroring the game's MapInfo.ReadMapHeader.
    /// `startPosition` allows parsing secondary campaign parts located deeper in the file;
    /// recorded ranges stay absolute to the containing file.
    let parse (data: byte[]) (startPosition: int) : SfdHeader =
        let reader = SfdBinaryReader(data, startPosition)

        let mutable guid = Guid.Empty
        let mutable version = None
        let mutable originalGuid = None
        let mutable ownerHash = None
        let mutable isTemplate = false
        let mutable editLock = false
        let mutable name = ""
        let mutable author = ""
        let mutable mapType = 0
        let mutable totalPlayers = 0
        let mutable tags = ""
        let mutable description = ""
        let mutable saveDate = None
        let mutable publishExternalId = ""
        let mutable officialMarker = None
        let mutable scriptTypes = None
        let mutable scriptSource = None
        let mutable hasThumbnail = false

        let mutable parts: SfdMapPart list = []

        let mutable versionRange = None
        let mutable publishExternalIdRange = None
        let mutable editLockByteRange = None
        let mutable officialMarkerRange = None
        let mutable scriptSourceRange = None
        let mutable extensionScriptTypesRange = None
        let mutable mapTypePlayersRange = None
        let mutable tagsRange = None
        let mutable templateByteRange = None
        let mutable partsTableRange = None

        let mutable headerEnd = -1

        while headerEnd < 0 do
            let tokenPosition = reader.Position
            let token = reader.ReadStringNonNull()

            if not (token.StartsWith "h") then
                headerEnd <- tokenPosition
            else
                match token with
                | Tokens.GuidVersion ->
                    guid <- reader.ReadGuid()

                    let v = reader.ReadStringNonNull()
                    version <- Some v
                    versionRange <- Some reader.LastRange

                | Tokens.OriginalGuid ->
                    originalGuid <- Some(reader.ReadGuid())
                    ownerHash <- Some(reader.ReadStringNonNull())

                | Tokens.Template ->
                    isTemplate <- reader.ReadBoolean()
                    templateByteRange <- Some reader.LastRange

                | Tokens.EditLock ->
                    editLock <- reader.ReadBoolean()
                    editLockByteRange <- Some reader.LastRange

                | Tokens.WorldName -> name <- reader.ReadStringNonNull()

                | Tokens.WorldAuthor -> author <- reader.ReadStringNonNull()

                | Tokens.MapTypePlayers ->
                    // Explicit span: LastRange would only cover the first integer.
                    let pairStart = reader.Position
                    mapType <- reader.ReadInt32()
                    totalPlayers <- reader.ReadInt32()
                    mapTypePlayersRange <- Some(ByteRange.create pairStart reader.Position)

                | Tokens.Tags ->
                    let t = reader.ReadStringNonNull()
                    tags <- t
                    tagsRange <- Some reader.LastRange

                | Tokens.Description -> description <- reader.ReadStringNonNull()

                | Tokens.SaveDate ->
                    let year = reader.ReadInt32()
                    let month = reader.ReadInt32()
                    let day = reader.ReadInt32()
                    let hour = reader.ReadInt32()
                    let minute = reader.ReadInt32()

                    try
                        saveDate <- Some(DateTime(year, month, day, hour, minute, 42))
                    with :? ArgumentOutOfRangeException ->
                        saveDate <- None

                | Tokens.PublishExternalId ->
                    publishExternalId <- reader.ReadStringNonNull()
                    publishExternalIdRange <- Some reader.LastRange

                | Tokens.EditorMarker ->
                    let chars = reader.ReadUtf8Chars(OfficialToken.CharLength)
                    officialMarker <- Some(chars.ToCharArray())
                    officialMarkerRange <- Some reader.LastRange

                | Tokens.ExtensionScriptTypes ->
                    let types = reader.ReadStringNonNull()
                    scriptTypes <- Some types
                    extensionScriptTypesRange <- Some reader.LastRange

                | Tokens.ExtensionScriptSource ->
                    scriptSource <- Some(reader.ReadStringNullDelimiter())
                    scriptSourceRange <- Some reader.LastRange

                | Tokens.PartsTable ->
                    let tableStart = reader.Position
                    let partCount = reader.ReadInt32()

                    if partCount < 0 || partCount > 1024 then
                        raise (
                            SfdFormatException
                                $"h_pt declares an implausible part count ({partCount}) at offset {tableStart}."
                        )

                    let rec readParts remaining acc =
                        if remaining = 0 then
                            List.rev acc
                        else
                            let name = reader.ReadStringNonNull()
                            let selectable = reader.ReadBoolean()
                            let startPosition = reader.ReadInt32()

                            readParts
                                (remaining - 1)
                                ({ Name = name
                                   Selectable = selectable
                                   StartPosition = startPosition }
                                 :: acc)

                    parts <- readParts partCount []
                    partsTableRange <- Some(ByteRange.create tableStart reader.Position)

                | Tokens.Thumbnail ->
                    let length = reader.ReadInt32()

                    if length < 0 || length > reader.Remaining then
                        raise (
                            SfdFormatException
                                $"h_img declares thumbnail size {length} which exceeds the remaining file length."
                        )

                    reader.Skip length
                    hasThumbnail <- true

                | other ->
                    raise (SfdFormatException $"Error: Header information '{other.TrimEnd('\n')}' could not be loaded.")

        let officialMatch =
            match officialMarker with
            | Some chars -> OfficialToken.matches name author chars
            | None -> false

        { empty with
            Guid = guid
            Version = version
            OriginalGuid = originalGuid
            OwnerHash = ownerHash
            IsTemplate = isTemplate
            EditLock = editLock
            Name = name
            Author = author
            MapType = mapType
            TotalPlayers = totalPlayers
            Tags = tags
            Description = description
            SaveDate = saveDate
            PublishExternalId = publishExternalId
            OfficialMarker = officialMarker
            IsOfficial = officialMatch
            IsExtensionScript = Option.isSome scriptSource
            ScriptTypes = scriptTypes
            ExtensionScriptTypesRange = extensionScriptTypesRange
            ScriptSource = scriptSource
            HasThumbnail = hasThumbnail
            Parts = parts
            VersionRange = versionRange
            PublishExternalIdRange = publishExternalIdRange
            EditLockByteRange = editLockByteRange
            OfficialMarkerRange = officialMarkerRange
            ScriptSourceRange = scriptSourceRange
            MapTypePlayersRange = mapTypePlayersRange
            TagsRange = tagsRange
            TemplateByteRange = templateByteRange
            PartsTableRange = partsTableRange
            HeaderEnd = headerEnd }
