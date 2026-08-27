namespace SFD.FileModifier.Core

open System

/// A single entry of a c_wp world property stream.
type WorldProperty =
    { Key: int
      Value: WorldPropertyValue
      ValueRange: ByteRange }

and WorldPropertyValue =
    | WpString of string
    | WpFloat of float32
    | WpInt of int
    | WpBool of bool

/// Parsed contents of one c_wp section together with the offsets needed for editing.
type WorldProperties =
    { Properties: WorldProperty list
      CountFieldRange: ByteRange
      SectionEnd: int }

module WorldProperties =

    let private typeTagOf (value: WorldPropertyValue) : int =
        match value with
        | WpString _ -> 0
        | WpFloat _ -> 1
        | WpInt _ -> 2
        | WpBool _ -> 3

    /// Parses the property stream starting right after the c_wp token has been read.
    let parseSection (reader: SfdBinaryReader) : WorldProperties =
        let countStart = reader.Position
        let count = reader.ReadInt32()
        let countRange = reader.LastRange

        if count < 0 then
            raise (
                SfdFormatException
                    $"c_wp section declares an invalid negative property count ({count}) at offset {countStart}."
            )

        let readValue () : WorldPropertyValue * ByteRange =
            match reader.ReadInt32() with
            | 0 ->
                let s = reader.ReadString()
                WpString s, reader.LastRange
            | 1 ->
                let f = reader.ReadSingle()
                WpFloat f, reader.LastRange
            | 2 ->
                let i = reader.ReadInt32()
                WpInt i, reader.LastRange
            | 3 ->
                let b = reader.ReadBoolean()
                WpBool b, reader.LastRange
            | other ->
                raise (
                    SfdFormatException
                        $"Unsupported world property value type {other} encountered after reading {reader.Position} bytes."
                )

        let rec loop remaining acc =
            if remaining = 0 then
                List.rev acc
            else
                let key = reader.ReadInt32()
                let value, range = readValue ()

                loop
                    (remaining - 1)
                    ({ Key = key
                       Value = value
                       ValueRange = range }
                     :: acc)

        { Properties = loop count []
          CountFieldRange = countRange
          SectionEnd = reader.Position }

    let tryFind (key: int) (properties: WorldProperties) : WorldProperty option =
        properties.Properties |> List.tryFind (fun p -> p.Key = key)

    let find (key: int) (properties: WorldProperties) : WorldProperty =
        match tryFind key properties with
        | Some p -> p
        | None -> raise (SfdPropertyNotFoundException key)

    /// Serialises a single property entry (key + type tag + value).
    let serializeEntry (key: int) (value: WorldPropertyValue) : byte[] =
        Array.concat
            [ SfdEncode.int32 key
              SfdEncode.int32 (typeTagOf value)
              (match value with
               | WpString s -> SfdEncode.string s
               | WpFloat f -> BitConverter.GetBytes f
               | WpInt i -> SfdEncode.int32 i
               | WpBool b -> SfdEncode.bool b) ]
