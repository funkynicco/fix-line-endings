using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FixLineEndings
{
    class Program
    {
        const byte ByteLineFeed = 0x0A;
        const byte ByteCarriageReturn = 0x0D;

        enum Result
        {
            NothingToDo,
            Fixed,
            NeedToBeFixed,
            CorruptedUtf16NotMultipleOfTwo,
            FailedToLoadFile,
            FailedToSaveFile,
            UnknownTextFileBOM,
        }

        static Result SubMain(string[] args)
        {
            var mode = LineEndingsMode.LineFeed;
            var backup = false;
            var dry = false; // does not apply any changes
            var retain_modified_date = false;
            string filename = null;

            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "--lf")
                {
                    mode = LineEndingsMode.LineFeed;
                }
                else if (args[i] == "--crlf")
                {
                    mode = LineEndingsMode.CarriageReturnLineFeed;
                }
                else if (args[i] == "--backup")
                {
                    backup = true;
                }
                else if (args[i] == "--dry")
                {
                    dry = true;
                }
                else if (args[i] == "--keep-modified")
                {
                    retain_modified_date = true;
                }
                else
                    filename = args[i];
            }

            byte[] bytes = null;
            DateTime lastWriteTimeUtc;
            try
            {
                bytes = File.ReadAllBytes(filename);
                lastWriteTimeUtc = new FileInfo(filename).LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load: {filename}");
                Console.WriteLine($"- {ex.Message}");
                return Result.FailedToLoadFile;
            }

            var fileEncoding = DetermineEncoding(bytes, 0, bytes.Length);
            if (fileEncoding == FileEncoding.UnknownTextFileBOM)
                return Result.UnknownTextFileBOM;

            var bytes_start_index = 0;
            var new_bytes = new byte[bytes.Length * 2];
            int new_bytes_index = 0;

            switch (fileEncoding)
            {
                case FileEncoding.Utf8:
                    new_bytes[new_bytes_index++] = bytes[bytes_start_index++];
                    new_bytes[new_bytes_index++] = bytes[bytes_start_index++];
                    new_bytes[new_bytes_index++] = bytes[bytes_start_index++];
                    break;
                case FileEncoding.Utf16BigEndian:
                case FileEncoding.Utf16LittleEndian:
                    new_bytes[new_bytes_index++] = bytes[bytes_start_index++];
                    new_bytes[new_bytes_index++] = bytes[bytes_start_index++];

                    if (((bytes.Length - bytes_start_index) & 1) != 0)
                        return Result.CorruptedUtf16NotMultipleOfTwo;

                    break;
            }

            for (int i = bytes_start_index; i < bytes.Length;)
            {
                if (fileEncoding == FileEncoding.Utf16BigEndian) // FE FF 00 49 00 44 00 53 00 5F 00 43 00 4C 00 49
                {
                    if (bytes[i] == 0 &&
                        bytes[i + 1] == ByteCarriageReturn) // 0x00, \r
                    {
                        if (i + 3 < bytes.Length &&
                            bytes[i + 2] == 0 &&
                            bytes[i + 3] == ByteLineFeed) // 0x00, \n
                        {
                            i += 2;
                        }

                        AppendLine(fileEncoding, mode, new_bytes, ref new_bytes_index);
                    }
                    else if (
                        bytes[i] == 0 &&
                        bytes[i + 1] == ByteLineFeed) // 0x00, \n
                    {
                        AppendLine(fileEncoding, mode, new_bytes, ref new_bytes_index);
                    }
                    else
                    {
                        new_bytes[new_bytes_index++] = bytes[i];
                        new_bytes[new_bytes_index++] = bytes[i + 1];
                    }

                    i += 2;
                }
                else if (fileEncoding == FileEncoding.Utf16LittleEndian) // FF FE 49 00 44 00 53 00 5F 00 43 00 4C 00 49 00
                {
                    if (bytes[i] == ByteCarriageReturn &&
                        bytes[i + 1] == 0) // \r, 0x00
                    {
                        if (i + 3 < bytes.Length &&
                            bytes[i + 2] == ByteLineFeed &&
                            bytes[i + 3] == 0) // \n, 0x00
                        {
                            i += 2;
                        }

                        AppendLine(fileEncoding, mode, new_bytes, ref new_bytes_index);
                    }
                    else if (
                        bytes[i] == ByteLineFeed &&
                        bytes[i + 1] == 0) // \n, 0x00
                    {
                        AppendLine(fileEncoding, mode, new_bytes, ref new_bytes_index);
                    }
                    else
                    {
                        new_bytes[new_bytes_index++] = bytes[i];
                        new_bytes[new_bytes_index++] = bytes[i + 1];
                    }

                    i += 2;
                }
                else
                {
                    if (bytes[i] == ByteCarriageReturn) // \r
                    {
                        if (i + 1 < bytes.Length &&
                            bytes[i + 1] == ByteLineFeed) // \n
                        {
                            ++i;
                        }

                        AppendLine(fileEncoding, mode, new_bytes, ref new_bytes_index);
                    }
                    else if (bytes[i] == ByteLineFeed) // \n
                    {
                        AppendLine(fileEncoding, mode, new_bytes, ref new_bytes_index);
                    }
                    else
                        new_bytes[new_bytes_index++] = bytes[i];

                    ++i;
                }
            }

            var changed = false;
            if (new_bytes_index == bytes.Length)
            {
                for (int i = 0; i < bytes.Length; ++i)
                {
                    if (bytes[i] != new_bytes[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }
            else
                changed = true;

            if (!changed)
                return Result.NothingToDo;

            if (dry)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{fileEncoding}] Needs to be fixed: {filename}");
                Console.ForegroundColor = ConsoleColor.Gray;
                return Result.NeedToBeFixed;
            }

            if (backup)
                File.Copy(filename, filename + ".bak", true);

            Stream stream;
            try
            {
                stream = File.Open(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save: {filename}");
                Console.WriteLine($"- {ex.Message}");
                return Result.FailedToSaveFile;
            }

            using (stream)
            {
                stream.Write(new_bytes, 0, new_bytes_index);
            }

            if (retain_modified_date)
                new FileInfo(filename).LastWriteTimeUtc = lastWriteTimeUtc;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{fileEncoding}] Fixed: {filename}");
            Console.ForegroundColor = ConsoleColor.Gray;
            return Result.Fixed;
        }

        static int Main(string[] args)
            => (int)SubMain(args);

        static FileEncoding DetermineEncoding(byte[] bytes, int offset, int length)
        {
            if (offset + 3 <= bytes.Length &&
                bytes[offset + 0] == 0xef &&
                bytes[offset + 1] == 0xbb &&
                bytes[offset + 2] == 0xbf)
                return FileEncoding.Utf8;

            if (offset + 2 <= bytes.Length)
            {
                if (bytes[offset + 0] == 0xfe &&
                    bytes[offset + 1] == 0xff)
                    return FileEncoding.Utf16BigEndian;

                if (bytes[offset + 0] == 0xff &&
                    bytes[offset + 1] == 0xfe)
                    return FileEncoding.Utf16LittleEndian;
            }

            //if (bytes[0] < 0x20 ||
            //    bytes[0] > 0xa0)
            //    return FileEncoding.UnknownTextFileBOM;


            return FileEncoding.Ansi;
        }

        static void AppendLine(FileEncoding encoding, LineEndingsMode mode, byte[] new_bytes, ref int new_bytes_index)
        {
            switch (mode)
            {
                case LineEndingsMode.LineFeed:
                    if (encoding == FileEncoding.Utf16BigEndian)
                    {
                        new_bytes[new_bytes_index++] = 0;
                        new_bytes[new_bytes_index++] = ByteLineFeed;
                    }
                    else if (encoding == FileEncoding.Utf16LittleEndian)
                    {
                        new_bytes[new_bytes_index++] = ByteLineFeed;
                        new_bytes[new_bytes_index++] = 0;
                    }
                    else
                        new_bytes[new_bytes_index++] = ByteLineFeed;
                    break;
                case LineEndingsMode.CarriageReturnLineFeed:
                    if (encoding == FileEncoding.Utf16BigEndian)
                    {
                        new_bytes[new_bytes_index++] = 0;
                        new_bytes[new_bytes_index++] = ByteCarriageReturn;
                        new_bytes[new_bytes_index++] = 0;
                        new_bytes[new_bytes_index++] = ByteLineFeed;
                    }
                    else if (encoding == FileEncoding.Utf16LittleEndian)
                    {
                        new_bytes[new_bytes_index++] = ByteCarriageReturn;
                        new_bytes[new_bytes_index++] = 0;
                        new_bytes[new_bytes_index++] = ByteLineFeed;
                        new_bytes[new_bytes_index++] = 0;
                    }
                    else
                    {
                        new_bytes[new_bytes_index++] = ByteCarriageReturn;
                        new_bytes[new_bytes_index++] = ByteLineFeed;
                    }
                    break;
            }
        }
    }

    enum LineEndingsMode
    {
        /// <summary>
        /// Equivalent of \n
        /// </summary>
        LineFeed,

        /// <summary>
        /// Equivalent of: \r\n
        /// </summary>
        CarriageReturnLineFeed,
    }

    enum FileEncoding
    {
        Ansi, // just text...
        Utf8, // 0xef, 0xbb, 0xbf
        Utf16BigEndian, // 0xfe, 0xff
        Utf16LittleEndian, // 0xff, 0xfe
        UnknownTextFileBOM
    }

    class TextFileCorrupted : Exception
    {
        public TextFileCorrupted(string message) :
            base(message)
        {
        }
    }
}
