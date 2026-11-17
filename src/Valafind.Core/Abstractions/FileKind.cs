namespace Valafind.Core.Abstractions;

/// <summary>Coarse file category used for filtering, icons and extractor selection.</summary>
public enum FileKind
{
    Other = 0,
    Document = 1,
    Pdf = 2,
    Spreadsheet = 3,
    Presentation = 4,
    Text = 5,
    Code = 6,
    Mail = 7,
    Notebook = 8,
    Image = 9,
    Audio = 10,
    Video = 11,
    Archive = 12,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = FileKind.Pdf,
        [".doc"] = FileKind.Document, [".docx"] = FileKind.Document, [".odt"] = FileKind.Document,
        [".rtf"] = FileKind.Document, [".pages"] = FileKind.Document, [".epub"] = FileKind.Document,
        [".xls"] = FileKind.Spreadsheet, [".xlsx"] = FileKind.Spreadsheet, [".ods"] = FileKind.Spreadsheet,
        [".csv"] = FileKind.Spreadsheet, [".tsv"] = FileKind.Spreadsheet, [".numbers"] = FileKind.Spreadsheet,
        [".ppt"] = FileKind.Presentation, [".pptx"] = FileKind.Presentation, [".odp"] = FileKind.Presentation,
        [".key"] = FileKind.Presentation,
        [".txt"] = FileKind.Text, [".md"] = FileKind.Text, [".markdown"] = FileKind.Text, [".rst"] = FileKind.Text,
        [".org"] = FileKind.Text, [".tex"] = FileKind.Text, [".log"] = FileKind.Text, [".html"] = FileKind.Text,
        [".htm"] = FileKind.Text, [".xml"] = FileKind.Text, [".json"] = FileKind.Text, [".yaml"] = FileKind.Text,
        [".yml"] = FileKind.Text, [".toml"] = FileKind.Text, [".ini"] = FileKind.Text,
        [".eml"] = FileKind.Mail, [".msg"] = FileKind.Mail, [".mbox"] = FileKind.Mail,
        [".ipynb"] = FileKind.Notebook,
        [".cs"] = FileKind.Code, [".fs"] = FileKind.Code, [".vb"] = FileKind.Code, [".py"] = FileKind.Code,
        [".js"] = FileKind.Code, [".mjs"] = FileKind.Code, [".ts"] = FileKind.Code, [".tsx"] = FileKind.Code,
        [".jsx"] = FileKind.Code, [".java"] = FileKind.Code, [".kt"] = FileKind.Code, [".go"] = FileKind.Code,
        [".rs"] = FileKind.Code, [".c"] = FileKind.Code, [".h"] = FileKind.Code, [".cpp"] = FileKind.Code,
        [".hpp"] = FileKind.Code, [".swift"] = FileKind.Code, [".rb"] = FileKind.Code, [".php"] = FileKind.Code,
        [".sh"] = FileKind.Code, [".ps1"] = FileKind.Code, [".sql"] = FileKind.Code, [".css"] = FileKind.Code,
        [".scss"] = FileKind.Code, [".axaml"] = FileKind.Code, [".xaml"] = FileKind.Code, [".lua"] = FileKind.Code,
        [".r"] = FileKind.Code, [".scala"] = FileKind.Code, [".dart"] = FileKind.Code,
        [".png"] = FileKind.Image, [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".gif"] = FileKind.Image,
        [".heic"] = FileKind.Image, [".webp"] = FileKind.Image, [".bmp"] = FileKind.Image, [".tif"] = FileKind.Image,
        [".tiff"] = FileKind.Image, [".svg"] = FileKind.Image, [".raw"] = FileKind.Image,
        [".heif"] = FileKind.Image, [".avif"] = FileKind.Image,
        [".mp3"] = FileKind.Audio, [".wav"] = FileKind.Audio, [".flac"] = FileKind.Audio, [".m4a"] = FileKind.Audio,
        [".ogg"] = FileKind.Audio, [".aac"] = FileKind.Audio, [".opus"] = FileKind.Audio, [".wma"] = FileKind.Audio,
        [".aif"] = FileKind.Audio, [".aiff"] = FileKind.Audio,
        [".mp4"] = FileKind.Video, [".mov"] = FileKind.Video, [".mkv"] = FileKind.Video, [".avi"] = FileKind.Video,
        [".webm"] = FileKind.Video, [".m4v"] = FileKind.Video, [".wmv"] = FileKind.Video, [".3gp"] = FileKind.Video,
        [".zip"] = FileKind.Archive, [".7z"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".tar"] = FileKind.Archive,
        [".gz"] = FileKind.Archive,
    };

    public static FileKind Classify(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : FileKind.Other;
}
