namespace SFD.FileModifier.Core

open System
open System.Text.RegularExpressions

/// Base exception for all operations performed by the library.
type SfdException(message: string) =
    inherit Exception(message)

/// Raised when a map or script file cannot be parsed according to the format.
type SfdFormatException(message: string) =
    inherit SfdException(message)

/// Raised when an argument value fails validation before being written.
type SfdValidationException(message: string) =
    inherit SfdException(message)

/// Raised when a file extension does not match the expected one.
type SfdExtensionException(expected: string, actual: string) =
    inherit SfdException($"Expected a {expected} file but got '{actual}'.")

/// Raised when a required header section is missing from the file.
type SfdHeaderNotFoundException(token: string) =
    inherit SfdException($"Required header information '{token.TrimEnd('\n')}' was not found in the file.")

/// Raised when a required world property is missing.
type SfdPropertyNotFoundException(propertyId: int) =
    inherit SfdException($"World property with id {propertyId} was not found in the file.")

[<RequireQualifiedAccess>]
module Tokens =

    [<Literal>]
    let GuidVersion = "h_gv"

    [<Literal>]
    let OriginalGuid = "h_or"

    [<Literal>]
    let Template = "h_tmp"

    [<Literal>]
    let EditLock = "h_el"

    [<Literal>]
    let WorldName = "h_wn"

    [<Literal>]
    let WorldAuthor = "h_wa"

    [<Literal>]
    let MapTypePlayers = "h_mtp"

    [<Literal>]
    let Tags = "h_tg"

    [<Literal>]
    let Description = "h_wd"

    [<Literal>]
    let SaveDate = "h_wdt"

    [<Literal>]
    let PublishExternalId = "h_pei"

    [<Literal>]
    let EditorMarker = "h_mt"

    [<Literal>]
    let ExtensionScriptTypes = "h_ext"

    /// The game stores this token including its trailing newline character.
    [<Literal>]
    let ExtensionScriptSource = "h_exscript\n"

    [<Literal>]
    let PartsTable = "h_pt"

    [<Literal>]
    let Thumbnail = "h_img"

    [<Literal>]
    let WorldPropertiesSection = "c_wp"

    /// In-map embedded script: Base64 encoded UTF-8 C# source.
    [<Literal>]
    let MapScriptSection = "c_scrpt"

    [<Literal>]
    let EndOfFile = "EOF"

    /// Value identifying files as editable by the map editor (not officially locked).
    [<Literal>]
    let EditorMarkerValue = "SFDMAPEDIT"

/// Categories a map or extension script can declare, matching the game's
/// MapType switch (0/1 -> Versus, 2 -> Custom, 3 -> Campaign,
/// 4 -> Survival, 5 -> Challenge).
type SfdMapCategory =
    | Versus
    | Custom
    | Campaign
    | Survival
    | Challenge
    /// Present in data but outside the known range; rejected by write operations.
    | UnknownCategory of int

module SfdMapCategory =

    let ofRaw (rawType: int) : SfdMapCategory =
        match rawType with
        | 0 | 1 -> Versus
        | 2 -> Custom
        | 3 -> Campaign
        | 4 -> Survival
        | 5 -> Challenge
        | other -> UnknownCategory other

    /// Writing uses the game's current convention where Versus = 1.
    let toInt (category: SfdMapCategory) : int =
        match category with
        | Versus -> 1
        | Custom -> 2
        | Campaign -> 3
        | Survival -> 4
        | Challenge -> 5
        | UnknownCategory raw -> raw

/// Custom tags a map can carry; h_tg / property Tags store their numeric ids.
type SfdTag =
    | AdventureMap
    | MeleeMap
    | BotSupport
    | Singleplayer
    | Multiplayer
    | OptimizedFor16Players
    | CustomizedGameplayRules
    | UnknownTag of int

module SfdTag =

    let idOf (tag: SfdTag) : int =
        match tag with
        | AdventureMap -> 1
        | MeleeMap -> 2
        | BotSupport -> 3
        | Singleplayer -> 4
        | Multiplayer -> 5
        | OptimizedFor16Players -> 6
        | CustomizedGameplayRules -> 7
        | UnknownTag id -> id

    let ofId (id: int) : SfdTag =
        match id with
        | 1 -> AdventureMap
        | 2 -> MeleeMap
        | 3 -> BotSupport
        | 4 -> Singleplayer
        | 5 -> Multiplayer
        | 6 -> OptimizedFor16Players
        | 7 -> CustomizedGameplayRules
        | other -> UnknownTag other

    /// "3,5,6" tolerated on read: blanks and unknown ids are preserved.
    let parseList (tags: string) : SfdTag list =
        if String.IsNullOrEmpty tags then []
        else
            tags.Split(',')
            |> Seq.choose (fun token ->
                match Int32.TryParse(token.Trim()) with
                | true, id -> Some(ofId id)
                | _ -> None)
            |> List.ofSeq

    /// Raises for unknown ids, mirroring the decision that writes reject them.
    let validate (tags: SfdTag list) : unit =
        for tag in tags do
            match tag with
            | UnknownTag id ->
                raise (
                    SfdValidationException $"Unknown tag id {id}. Known ids are 1-7 (Adventure Map, Melee Map, Bot Support, Singleplayer, Multiplayer, Optimized For 16 Players, Customized Gameplay/Rules)."
                )
            | _ -> ()

/// Formats and validates the comma separated availability string that the
/// game stores as world property ScriptTypes (339) and, for extension
/// scripts, mirrors inside the h_ext header section.
[<RequireQualifiedAccess>]
module SfdGameModes =

    let Known = [ "Versus"; "Custom"; "Campaign"; "Survival" ]

    let parse (modes: string) : string list =
        if String.IsNullOrEmpty modes then []
        else
            modes.Split(',')
            |> Seq.map (fun token -> token.Trim())
            |> Seq.filter (fun token -> token.Length > 0)
            |> List.ofSeq

    let validate (modes: string list) : unit =
        for mode in modes do
            if not (List.exists (fun known -> String.Equals(known, mode, StringComparison.Ordinal)) Known) then
                let knownModes = String.Join(", ", Known)

                raise (
                    SfdValidationException $"Unknown game mode '{mode}'. Known modes are {knownModes}."
                )

    let format (modes: string list) : string = String.Join(",", modes)

/// Numeric ids of the world property stream entries (section c_wp).
[<RequireQualifiedAccess>]
module WorldPropertyIds =

    [<Literal>]
    let MapName = 2

    [<Literal>]
    let MapAuthor = 3

    [<Literal>]
    let CameraArea = 8

    [<Literal>]
    let Bottom = 9

    [<Literal>]
    let Weather = 12

    [<Literal>]
    let StartCommands = 61

    [<Literal>]
    let MapType = 103

    [<Literal>]
    let ActiveCameraArea = 258

    [<Literal>]
    let TotalPlayers = 262

    [<Literal>]
    let AutoFillWithBots = 330

    [<Literal>]
    let IsTemplate = 331

    [<Literal>]
    let EditLock = 332

    [<Literal>]
    let PublishExternalId = 333

    [<Literal>]
    let Description = 334

    [<Literal>]
    let Tags = 337

    [<Literal>]
    let ScriptTypes = 339

[<RequireQualifiedAccess>]
module OfficialToken =

    /// Number of characters of every official lock token, matching ReadChars(10).
    [<Literal>]
    let CharLength = 10

    /// Computes the official lock token characters for the given header.
    /// Mirrors Superfighters Deluxe `MapInfo.CalcOfficialMap(header)`.
    let computeChars (header: string) : char[] =
        let array = "0123456789".ToCharArray()
        for i in 0 .. header.Length - 1 do
            let idx = i % array.Length
            array[idx] <- char (int array[idx] + int header[idx])
        array[0] <- '1'
        array

    /// Computes the UTF-8 encoded official lock token bytes for a map name and author.
    let computeBytes (name: string) (author: string) : byte[] =
        computeChars (name + author) |> System.Text.Encoding.UTF8.GetBytes

    /// Checks whether the given token characters match the ones computed for name and author.
    let matches (name: string) (author: string) (tokenChars: char[]) : bool =
        tokenChars.Length = CharLength
        && Array.compareWith (fun (a: char) (b: char) -> compare a b) (computeChars (name + author)) tokenChars = 0

[<RequireQualifiedAccess>]
module Validation =

    // Covers every version style observed in real files and the game itself:
    // v.1.3.7d | v.1.4.0 | v.1.5.14.0 | v.1.6.0.1
    let private versionRegex =
        Regex(@"^v\.\d+(\.\d+){1,3}[a-z]?$", RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

    let publishId (publishId: string) : unit =
        if String.IsNullOrEmpty publishId then
            raise (SfdValidationException "Publish ID must be at least 10 digits long and contain only numeric characters.")
        elif publishId.Length < 10 then
            raise (SfdValidationException $"Publish ID must be at least 10 digits long and contain only numeric characters. Got length {publishId.Length}.")
        elif not (publishId |> Seq.forall Char.IsDigit) then
            raise (SfdValidationException "Publish ID must be at least 10 digits long and contain only numeric characters. Non-digit characters found.")

    let versionCode (versionCode: string) : unit =
        if String.IsNullOrEmpty versionCode then
            raise (SfdValidationException "Version code must match 'v.<digits>(.<digits>)*' with an optional trailing letter, e.g. v.1.6.0.1.")
        elif not (versionRegex.IsMatch versionCode) then
            raise (SfdValidationException $"'{versionCode}' is not a valid version code. Expected e.g. v.1.3.7d, v.1.4.0 or v.1.6.0.1.")

    /// Rejects null text and embedded null characters.
    let nullFreeText (text: string) (fieldName: string) : unit =
        if isNull text then
            raise (SfdValidationException $"{fieldName} cannot be null.")
        elif text.Contains('\u0000') then
            raise (SfdValidationException $"{fieldName} cannot contain null characters.")

    /// Validates strings like "240,-320,-240,320": four comma separated floats.
    let cameraArea (cameraArea: string) : unit =
        nullFreeText cameraArea "Camera area"

        let tokens = cameraArea.Split(',')

        let parseable =
            tokens.Length = 4
            && tokens
            |> Seq.forall (fun token ->
                System.Single.TryParse(
                    token,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture
                )
                |> fst)

        if not parseable then
            raise (
                SfdValidationException
                    $"Camera area '{cameraArea}' is invalid. Expected four comma separated numbers, e.g. '240,-320,-240,320'."
            )

    /// Validates strings that must parse as a single float ("-250").
    let floatText (text: string) (fieldName: string) : unit =
        nullFreeText text fieldName

        let ok =
            System.Single.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            |> fst

        if not ok then
            raise (SfdValidationException $"{fieldName} '{text}' is not a valid number.")
