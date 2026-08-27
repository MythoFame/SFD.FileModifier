namespace SFD.FileModifier.Core

open System
open System.Text

/// Inclusive start / exclusive end span of bytes inside a file.
[<Struct>]
type ByteRange =
    { Start: int
      End: int }

    member this.Length = this.End - this.Start

module ByteRange =

    let create start endInclusiveExclusive =
        { Start = start
          End = endInclusiveExclusive }

    let ofBounds (start: int) (length: int) = { Start = start; End = start + length }

/// Byte-array cursor mirroring the semantics of the game's SFDBinaryReader.
///
/// Every logical read updates `LastRange` to the exact byte span that was consumed,
/// so callers can record splicable regions while parsing sequentially.
type SfdBinaryReader(data: byte[], ?initialPosition: int) =

    let mutable position = defaultArg initialPosition 0
    let mutable lastStart = position
    let mutable lastEnd = position

    let require count =
        if data.Length - position < count then
            raise (
                SfdFormatException(
                    $"Unexpected end of file at offset {position}: needed {count} more byte(s) but only {data.Length - position} remain."
                )
            )

    member _.Position = position

    member _.Remaining = data.Length - position

    member _.LastRange: ByteRange = { Start = lastStart; End = lastEnd }

    member this.Skip(count: int) =
        if count < 0 then
            raise (SfdFormatException $"Cannot skip a negative number of bytes ({count}).")

        require count
        this.MarkElement()
        position <- position + count
        lastEnd <- position

    member _.MarkElement() = lastStart <- position

    /// Reads the next single byte.
    member this.ReadByte() =
        require 1
        this.MarkElement()
        lastEnd <- position + 1
        let b = data[position]
        position <- position + 1
        b

    /// Reads exactly `count` raw bytes.
    member this.ReadBytes(count: int) =
        require count
        this.MarkElement()
        let result = Array.sub data position count
        position <- position + count
        lastEnd <- position
        result

    member this.ReadInt32() =
        require 4
        this.MarkElement()
        lastEnd <- position + 4
        let value = BitConverter.ToInt32(data, position)
        position <- position + 4
        value

    member this.ReadSingle() =
        require 4
        this.MarkElement()
        lastEnd <- position + 4
        let value = BitConverter.ToSingle(data, position)
        position <- position + 4
        value

    member this.ReadBoolean() = this.ReadByte() <> 0uy

    member this.ReadGuid() =
        require 16
        this.MarkElement()
        lastEnd <- position + 16
        let value = Guid(data[position .. position + 15])
        position <- position + 16
        value

    member private this.Read7BitInt() =
        let mutable result = 0
        let mutable shift = 0
        let mutable continueReading = true

        while continueReading do
            let b = this.ReadByte()
            result <- result ||| ((int b &&& 0x7F) <<< shift)

            if b &&& 0x80uy = 0uy then
                continueReading <- false
            else
                shift <- shift + 7

            if shift > 35 then
                raise (SfdFormatException $"Malformed 7-bit encoded length at offset {lastStart}.")

        result

    /// Reads a length-prefixed UTF-8 string, as written by BinaryWriter.Write(string).
    member this.ReadString() =
        let start = position
        let length = this.Read7BitInt()
        require length
        let text = Encoding.UTF8.GetString(data, position, length)
        position <- position + length
        lastStart <- start
        lastEnd <- position
        text

    /// Reads a length-prefixed UTF-8 string, normalising null to empty (game's ReadStringNonNull).
    member this.ReadStringNonNull() =
        let text = this.ReadString()

        if isNull text then "" else text

    /// Reads bytes until a null delimiter; the delimiter is consumed and included in LastRange.
    member this.ReadStringNullDelimiter() =
        let start = position
        let mutable acc = ResizeArray()
        let mutable terminatorFound = false

        while not terminatorFound do
            require 1
            let b = data[position]
            position <- position + 1

            if b = 0uy then terminatorFound <- true else acc.Add b

        lastStart <- start
        lastEnd <- position
        Encoding.UTF8.GetString(acc.ToArray())

    /// Reads `charCount` UTF-8 characters like BinaryReader.ReadChars, which does not use a
    /// length prefix. The consumed byte span varies from charCount up to four times that value.
    member this.ReadUtf8Chars(charCount: int) =
        if charCount < 0 then
            raise (SfdFormatException $"Cannot read {charCount} characters.")
        elif charCount = 0 then
            ""
        else
            let start = position
            let sb = StringBuilder(charCount)
            let mutable charsRead = 0

            while charsRead < charCount do
                require 1
                let lead = int data[position]

                let byteLength =
                    if lead &&& 0xF8 = 0xF0 then
                        4
                    elif lead &&& 0xF0 = 0xE0 then
                        3
                    elif lead &&& 0xE0 = 0xC0 then
                        2
                    elif lead &&& 0x80 = 0x00 then
                        1
                    else
                        raise (
                            SfdFormatException
                                $"Invalid UTF-8 lead byte 0x{lead:X2} at offset {position} while reading {charCount} characters."
                        )

                require byteLength
                sb.Append(Encoding.UTF8.GetString(data, position, byteLength)) |> ignore
                position <- position + byteLength
                charsRead <- charsRead + 1

            lastStart <- start
            lastEnd <- position
            sb.ToString()

[<RequireQualifiedAccess>]
module SfdEncode =

    /// Encodes an integer using the same 7-bit scheme as BinaryWriter.Write7BitEncodedInt.
    let sevenBitInt (value: int) : byte[] =
        let mutable v = value
        let buffer = ResizeArray<byte>()

        while v >= 0x80 do
            buffer.Add(byte ((v &&& 0x7F) ||| 0x80))
            v <- v >>> 7

        buffer.Add(byte v)
        buffer.ToArray()

    let int32 (value: int) : byte[] = BitConverter.GetBytes(value)

    let bool (value: bool) : byte[] = [| if value then 1uy else 0uy |]

    let utf8 (text: string) : byte[] = Encoding.UTF8.GetBytes(text)

    /// Length-prefixed UTF-8 string, matching BinaryWriter.Write(string).
    let string (text: string) : byte[] =
        let payload = if isNull text then [||] else utf8 text

        Array.append (sevenBitInt payload.Length) payload

    /// Null-delimited UTF-8 string (trailing zero included), matching WriteStringNullDelimiter.
    let nullDelimitedString (text: string) : byte[] =
        let payload = if String.IsNullOrEmpty text then [||] else utf8 text

        Array.append payload [| 0uy |]

[<RequireQualifiedAccess>]
module SfdIo =

    let readAllBytes (path: string) : byte[] =
        try
            System.IO.File.ReadAllBytes path
        with
        | :? System.ArgumentException -> raise (SfdException $"'{path}' is not a valid file path.")
        | :? System.IO.FileNotFoundException -> raise (SfdException $"File not found: '{path}'.")
        | :? System.Security.SecurityException
        | :? UnauthorizedAccessException -> raise (SfdException $"Access to '{path}' was denied.")
