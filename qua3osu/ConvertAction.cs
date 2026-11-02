using System.CommandLine;
using System.Globalization;
using System.IO.Compression;
using qua3osu.Osu;
using Quaver.API.Maps;

namespace qua3osu;

public class ConvertAction
{
    private readonly ConvertCommand _command;
    private ParseResult? _result;

    private string[] Input => _result.GetValue(_command.Input);
    private string? Output => _result.GetValue(_command.Output);
    private double Od => _result.GetValue(_command.Od);
    private double Hp => _result.GetValue(_command.Hp);
    private int Volume => _result.GetValue(_command.Volume);
    private string? Creator => _result.GetValue(_command.Creator);
    private string SampleSet => _result.GetValue(_command.SampleSet);
    private int Offset => _result.GetValue(_command.Offset);
    private int Verbosity => _result.GetValue(_command.Verbosity);

    public ConvertAction(ConvertCommand command)
    {
        _command = command;
    }

    public void Convert(ParseResult res)
    {
        _result = res;

        var listOfFiles = new List<string>();
        foreach (var inputPath in Input)
        {
            if (Directory.Exists(inputPath))
            {
                listOfFiles.AddRange(Directory.GetFiles(inputPath).Where(CanConvertPath));
            }
            else if (File.Exists(inputPath) && CanConvertPath(inputPath))
            {
                listOfFiles.Add(inputPath);
            }
        }

        if (listOfFiles.Count == 0)
            Print("No files found");
        else
            Print($"Found {listOfFiles.Count} maps or mapsets to convert", 2);

        foreach (var path in listOfFiles)
        {
            Print($"Converting {path}", 2);
            try
            {
                if (Path.GetExtension(path) == ".qp")
                {
                    ConvertMapset(path);
                    Console.WriteLine($"Finished converting mapset {path}", 2);
                }
                else
                {
                    ConvertMapFile(path);
                    Console.WriteLine($"Finished converting map {path}", 2);
                }
            }
            catch (Exception e)
            {
                Print($"Could not convert map or mapset {path}, {e.Message}");
            }
        }
    }

    void ConvertMapset(string mapsetPath)
    {
        if (!File.Exists(mapsetPath) || Path.GetExtension(mapsetPath) != ".qp")
        {
            throw new ArgumentException("Invalid file");
        }

        var outputDir = Output ?? Path.GetDirectoryName(mapsetPath);
        var folderName = Path.GetFileNameWithoutExtension(mapsetPath);
        var extractDir = Path.Join(outputDir, folderName);

        try
        {
            ZipFile.ExtractToDirectory(mapsetPath, extractDir, true);
            foreach (var file in Directory.EnumerateFiles(extractDir))
            {
                switch (Path.GetExtension(file))
                {
                    case ".qua":
                        var osuPath = Path.Join(extractDir, Path.GetFileNameWithoutExtension(file) + ".osu");

                        var qua = Qua.Parse(file);
                        Print("Parsed qua", 3);

                        WriteMaps(qua, osuPath);
                        break;
                    case ".png":
                    case ".jpg":
                    case ".mp3":
                        Print($"Kept file {file} in directory", 3);
                        break;
                    default:
                        Print($"Removed file {file}", 3);
                        File.Delete(file);
                        break;
                }
            }

            var oszPath = extractDir + ".osz";
            if (File.Exists(oszPath))
            {
                Print($"Removed existing .osz", 3);
                File.Delete(oszPath);
            }

            ZipFile.CreateFromDirectory(extractDir, extractDir + ".osz");
            Print($"Created new .osz", 3);
        }
        catch (Exception e)
        {
            throw new Exception(e.Message, e);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(extractDir))
                File.Delete(file);
            Directory.Delete(extractDir);
            Print($"Removed temporary conversion folder", 3);
        }
    }

    void ConvertMapFile(string filePath)
    {
        if (!File.Exists(filePath) || Path.GetExtension(filePath) != ".qua")
        {
            throw new ArgumentException("Invalid file");
        }

        var outputDir = Output ?? Path.GetDirectoryName(filePath);
        var osuPath = Path.Join(outputDir, Path.GetFileNameWithoutExtension(filePath) + ".osu");

        var qua = Qua.Parse(filePath);
        Print("Parsed qua", 3);

        WriteMaps(qua, osuPath);
    }

    void WriteMaps(Qua qua, string osuPath)
    {
        var names = qua.Bookmarks.Select(b => b.Note)
            .Where(note => note == "$osu +nsv" || note?.StartsWith("$osu +nsv ", StringComparison.Ordinal) == true)
            .Select(note => note == "$osu +nsv" ? "" : note!["$osu +nsv ".Length..]).ToList();
        if (names.Any(name => name.Length == 0))
            throw new FormatException("$osu +nsv requires a difficulty name");

        var map = Qua2Osu(qua);
        File.WriteAllText(osuPath, map.ToString());
        Print($"Written to path {osuPath}", 3);

        for (var i = 0; i < names.Count; i++)
        {
            var noSv = Qua2Osu(qua, true);
            noSv.MetadataSection.Version = names[i];
            var path = Path.Join(Path.GetDirectoryName(osuPath),
                Path.GetFileNameWithoutExtension(osuPath) + $" (nsv{(i == 0 ? "" : $" {i + 1}")}).osu");
            File.WriteAllText(path, noSv.ToString());
            Print($"Written to path {path}", 3);
        }
    }

    OsuBeatmap Qua2Osu(Qua qua, bool noSv = false)
    {
        var offset = Offset;
        var svScale = 1d;
        foreach (var bookmark in qua.Bookmarks)
        {
            const string prefix = "$osu sv_scale";
            if (bookmark.Note != prefix && bookmark.Note?.StartsWith(prefix + " ", StringComparison.Ordinal) != true)
                continue;

            var value = bookmark.Note.Length == prefix.Length ? "" : bookmark.Note[(prefix.Length + 1)..];
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out svScale) ||
                !double.IsFinite(svScale) || svScale <= 0)
                throw new FormatException($"Invalid $osu sv_scale at {bookmark.StartTime}: {value}");
        }

        // apply modifications based on command arguments
        var map = new OsuBeatmap(qua, noSv, svScale)
        {
            GeneralSection =
            {
                SampleSet = SampleSet switch
                {
                    "normal" => "Normal",
                    "drum" => "Drum",
                    _ => "Soft"
                }
            },
            DifficultySection =
            {
                OverallDifficulty = Od,
                HpDrainRate = Hp
            },
        };
        map.MetadataSection.Creator = Creator ?? map.MetadataSection.Creator;

        var kiaiTimes = new List<int>();
        foreach (var bookmark in qua.Bookmarks)
        {
            if (bookmark.Note?.StartsWith("$osu ", StringComparison.Ordinal) != true)
                continue;

            var directive = bookmark.Note["$osu ".Length..];
            var separator = directive.IndexOf(' ');
            var command = separator < 0 ? directive : directive[..separator];
            var value = separator < 0 ? "" : directive[(separator + 1)..];
            switch (command)
            {
                case "hp":
                case "od":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        !double.IsFinite(number) || number is < 0 or > 10)
                        throw new FormatException($"Invalid $osu {command} at {bookmark.StartTime}: {value}");
                    if (command == "hp") map.DifficultySection.HpDrainRate = number;
                    else map.DifficultySection.OverallDifficulty = number;
                    break;
                case "kiai":
                    kiaiTimes.Add(bookmark.StartTime);
                    break;
                case "original_name":
                    map.MetadataSection.TitleUnicode = value;
                    break;
                case "original_artist":
                    map.MetadataSection.ArtistUnicode = value;
                    break;
            }
        }

        map.TimingPointsSection.AddKiai(kiaiTimes);

        map.GeneralSection.PreviewTime += offset;
        map.EditorSection.Bookmarks = qua.Bookmarks
            .Where(b => b.Note?.StartsWith("$osu ", StringComparison.Ordinal) != true)
            .Select(b => b.StartTime + offset).ToList();
        map.HitObjectsSection.HitObjects.ForEach(h =>
        {
            h.Time += offset;
            if (h.IsLongNote)
                h.EndTime += offset;
        });
        var vol = Volume;
        map.TimingPointsSection.TimingPoints.ForEach(tp =>
        {
            tp.Time += offset;
            tp.Volume = vol;
        });

        return map;
    }

    private bool CanConvertPath(string path) => Path.GetExtension(path) == ".qp" || Path.GetExtension(path) == ".qua";

    private void Print(string message, int minVerbosity = 1)
    {
        if (Verbosity >= minVerbosity)
            Console.WriteLine(message);
    }
}
