using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace BrPatchHub;

public sealed record NotebookOperationResult(int Found, int Changed, int Skipped, int TextsChanged, NotebookMigrationRecord? Record = null);

public static class LuciusNotebookMigration
{
    public const string TranslationId = "lucius-iii-ptbr";
    public const string ModelRelativePath = "Lucius3_Data/StreamingAssets/Notebook.xml";
    public const string EnglishCsvRelativePath = "Lucius3_Data/StreamingAssets/Localization/English.csv";
    public const string PortugueseCsvRelativePath = "Lucius3_Data/StreamingAssets/Localization/Português.csv";
    private const string BackupFolder = "_Backup_LuciusIII_PTBR";

    static LuciusNotebookMigration() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string DefaultPersistentRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Shiver Games", "Lucius III");

    public static bool AppliesTo(string translationId) => translationId.Equals(TranslationId, StringComparison.OrdinalIgnoreCase);

    public static void ValidateModel(string modelPath)
    {
        var model = Load(modelPath);
        ValidateRoot(model, model);
        var map = BuildTextMap(model);
        if (map.Count == 0) throw new InvalidDataException("O Notebook.xml PT-BR não possui nós <text>.");
    }

    public static async Task<NotebookOperationResult> MigrateAsync(
        string modelPath,
        string persistentRoot,
        string installationKey,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ValidateModel(modelPath);
        var record = new NotebookMigrationRecord { PersistentRoot = Path.GetFullPath(persistentRoot), ModelRelativePath = ModelRelativePath };
        if (!Directory.Exists(persistentRoot))
        {
            log("Nenhum Notebook persistente encontrado; a pasta de saves ainda não existe.");
            return new NotebookOperationResult(0, 0, 0, 0, record);
        }

        var backupRoot = Path.Combine(persistentRoot, BackupFolder, "NotebookMigration", installationKey);
        record.BackupRoot = backupRoot;
        var files = Directory.EnumerateFiles(persistentRoot, "Notebook.xml", SearchOption.AllDirectories)
            .Where(path => !FileTools.IsInside(Path.Combine(persistentRoot, BackupFolder), path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = files.Count;
        var changed = 0;
        var skipped = 0;
        var textsChanged = 0;
        var model = Load(modelPath);
        var modelTexts = BuildTextMap(model);

        try
        {
            foreach (var target in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log($"Notebook persistente encontrado: {target}");
                try
                {
                    var current = Load(target);
                    EnsureCompatible(model, current, modelTexts, out var currentTexts);
                    var relative = FileTools.RelativeTo(persistentRoot, target);
                    var backup = FileTools.ResolveInside(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    if (!File.Exists(backup)) File.Copy(target, backup);
                    log($"Backup criado: {backup}");

                    var before = NonTextFingerprint(current);
                    var count = 0;
                    foreach (var pair in modelTexts)
                    {
                        if (currentTexts[pair.Key].Value == pair.Value.Value) continue;
                        currentTexts[pair.Key].Value = pair.Value.Value;
                        count++;
                    }
                    await SaveValidatedAsync(current, target, before, modelTexts, cancellationToken);
                    record.Files.Add(new NotebookMigrationFile { RelativePath = relative, BackupPath = relative, TextCount = modelTexts.Count });
                    changed++;
                    textsChanged += count;
                    log($"{count} textos atualizados: {relative}");
                }
                catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    log($"Arquivo incompatível ignorado: {target} — {ex.Message}");
                }
            }
        }
        catch
        {
            await RestoreExactBackupsAsync(record, log, CancellationToken.None);
            throw;
        }
        log($"Migração concluída: {found} encontrado(s), {changed} migrado(s), {skipped} ignorado(s), {textsChanged} texto(s) atualizado(s). Nenhum arquivo .sav foi escrito.");
        return new NotebookOperationResult(found, changed, skipped, textsChanged, record);
    }

    public static Task<NotebookOperationResult> VerifyAsync(
        string modelPath,
        NotebookMigrationRecord record,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var model = Load(modelPath);
        var modelTexts = BuildTextMap(model);
        var found = 0;
        var healthy = 0;
        var skipped = 0;
        foreach (var item in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = FileTools.ResolveInside(record.PersistentRoot, item.RelativePath);
            if (!File.Exists(target)) { skipped++; log($"Arquivo incompatível ignorado na verificação: ausente — {target}"); continue; }
            found++;
            try
            {
                var current = Load(target);
                EnsureCompatible(model, current, modelTexts, out var currentTexts);
                if (modelTexts.All(x => currentTexts[x.Key].Value == x.Value.Value)) healthy++;
                else { skipped++; log($"Notebook persistente com textos divergentes: {target}"); }
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                skipped++;
                log($"Arquivo incompatível ignorado na verificação: {target} — {ex.Message}");
            }
        }
        return Task.FromResult(new NotebookOperationResult(found, healthy, skipped, 0));
    }

    public static async Task<NotebookOperationResult> RevertTextsAsync(
        string translatedModelPath,
        NotebookMigrationRecord record,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var translated = Load(translatedModelPath);
        var translatedTexts = BuildTextMap(translated);
        var found = 0;
        var changed = 0;
        var skipped = 0;
        var textsChanged = 0;
        foreach (var item in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = FileTools.ResolveInside(record.PersistentRoot, item.RelativePath);
            var backup = FileTools.ResolveInside(record.BackupRoot, item.BackupPath);
            if (!File.Exists(target) || !File.Exists(backup)) { skipped++; log($"Reversão segura ignorada: arquivo ou backup ausente — {item.RelativePath}"); continue; }
            found++;
            try
            {
                var current = Load(target);
                var original = Load(backup);
                EnsureCompatible(translated, current, translatedTexts, out var currentTexts);
                EnsureCompatible(translated, original, translatedTexts, out var originalTexts);
                var before = NonTextFingerprint(current);
                var count = 0;
                foreach (var pair in translatedTexts)
                {
                    var currentText = currentTexts[pair.Key];
                    if (currentText.Value != pair.Value.Value) continue;
                    if (currentText.Value == originalTexts[pair.Key].Value) continue;
                    currentText.Value = originalTexts[pair.Key].Value;
                    count++;
                }
                await SaveValidatedAsync(current, target, before, null, cancellationToken);
                changed++;
                textsChanged += count;
                log($"Textos persistentes revertidos com segurança: {item.RelativePath} — {count} texto(s). Progresso preservado.");
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                skipped++;
                log($"Reversão segura ignorada: {item.RelativePath} — {ex.Message}. O arquivo não foi sobrescrito.");
            }
        }
        return new NotebookOperationResult(found, changed, skipped, textsChanged);
    }

    public static async Task RestoreExactBackupsAsync(NotebookMigrationRecord record, Action<string> log, CancellationToken cancellationToken)
    {
        foreach (var item in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = FileTools.ResolveInside(record.PersistentRoot, item.RelativePath);
            var backup = FileTools.ResolveInside(record.BackupRoot, item.BackupPath);
            if (!File.Exists(backup)) { log($"Rollback persistente ignorado: backup ausente — {item.RelativePath}"); continue; }
            var temporary = target + $".brpatchhub-rollback-{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await FileTools.CopyAsync(backup, temporary, null, "Rollback do caderno persistente", cancellationToken);
                if (await FileTools.Sha256Async(temporary, cancellationToken) != await FileTools.Sha256Async(backup, cancellationToken))
                    throw new InvalidDataException("A cópia de rollback do Notebook.xml falhou na validação.");
                File.Move(temporary, target, true);
                log($"Rollback persistente concluído: {item.RelativePath}");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static XDocument Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Notebook.xml não encontrado.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void ValidateRoot(XDocument model, XDocument current)
    {
        var modelRoot = model.Root ?? throw new InvalidDataException("O modelo não possui elemento raiz.");
        var currentRoot = current.Root ?? throw new InvalidDataException("O arquivo não possui elemento raiz.");
        if (modelRoot.Name.LocalName != "NotebookData" || currentRoot.Name.LocalName != "NotebookData") throw new InvalidDataException("A raiz esperada NotebookData não foi encontrada.");
        var modelVersion = modelRoot.Attributes().FirstOrDefault(x => x.Name.LocalName == "version")?.Value;
        var currentVersion = currentRoot.Attributes().FirstOrDefault(x => x.Name.LocalName == "version")?.Value;
        if (string.IsNullOrWhiteSpace(modelVersion) || modelVersion != currentVersion) throw new InvalidDataException("A versão do Notebook.xml é incompatível.");
    }

    private static void EnsureCompatible(XDocument model, XDocument current, Dictionary<string, XElement> modelTexts, out Dictionary<string, XElement> currentTexts)
    {
        ValidateRoot(model, current);
        currentTexts = BuildTextMap(current);
        if (modelTexts.Count != currentTexts.Count || !modelTexts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(currentTexts.Keys))
            throw new InvalidDataException($"Conjunto de nós <text> incompatível (modelo: {modelTexts.Count}; arquivo: {currentTexts.Count}).");
        foreach (var pair in modelTexts)
            if (TechnicalSignature(pair.Value.Value) != TechnicalSignature(currentTexts[pair.Key].Value))
                throw new InvalidDataException($"Marcadores técnicos incompatíveis no nó {pair.Key}.");
    }

    private static Dictionary<string, XElement> BuildTextMap(XDocument document)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in document.Descendants().Where(x => x.Name.LocalName == "text"))
        {
            if (element.Elements().Any()) throw new InvalidDataException("Um nó <text> contém elementos internos e não pode ser alterado com segurança.");
            var key = StableKey(element);
            if (!result.TryAdd(key, element)) throw new InvalidDataException($"Chave duplicada de nó <text>: {key}");
        }
        return result;
    }

    private static string TechnicalSignature(string value)
    {
        var tokens = Regex.Matches(value, @"<br\s*/?>|\*|\+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(x => x.Value.StartsWith("<", StringComparison.Ordinal) ? "<br>" : x.Value);
        return string.Join("|", tokens);
    }

    private static string StableKey(XElement element)
    {
        var segments = element.AncestorsAndSelf().Reverse().Select(current =>
        {
            var id = current.Attributes().FirstOrDefault(x => x.Name.LocalName == "id")?.Value ?? "";
            var index = current.Parent is null ? 1 : current.Parent.Elements(current.Name)
                .Where(x => (x.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "") == id)
                .TakeWhile(x => !ReferenceEquals(x, current)).Count() + 1;
            var encodedId = Convert.ToBase64String(Encoding.UTF8.GetBytes(id));
            return $"{current.Name}[id={encodedId};index={index}]";
        });
        return "/" + string.Join("/", segments);
    }

    private static string NonTextFingerprint(XDocument document)
    {
        var builder = new StringBuilder();
        foreach (var node in document.Nodes()) AppendNode(node, builder, false);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendNode(XNode node, StringBuilder builder, bool ignoreTextContent)
    {
        switch (node)
        {
            case XElement element:
                var ignore = ignoreTextContent || element.Name.LocalName == "text";
                builder.Append("E:").Append(element.Name).Append('|');
                foreach (var attribute in element.Attributes().OrderBy(x => x.Name.ToString(), StringComparer.Ordinal))
                    builder.Append("A:").Append(attribute.Name).Append('=').Append(attribute.Value).Append('|');
                foreach (var child in element.Nodes()) AppendNode(child, builder, ignore);
                builder.Append("/E|");
                break;
            case XText text when !ignoreTextContent: builder.Append("T:").Append(text.Value).Append('|'); break;
            case XCData cdata when !ignoreTextContent: builder.Append("C:").Append(cdata.Value).Append('|'); break;
            case XComment comment: builder.Append("M:").Append(comment.Value).Append('|'); break;
            case XProcessingInstruction instruction: builder.Append("P:").Append(instruction.Target).Append('=').Append(instruction.Data).Append('|'); break;
        }
    }

    private static async Task SaveValidatedAsync(XDocument document, string target, string beforeFingerprint, Dictionary<string, XElement>? expectedTexts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = target + $".brpatchhub-{Guid.NewGuid():N}.tmp";
        try
        {
            var encodingName = document.Declaration?.Encoding;
            var encoding = string.IsNullOrWhiteSpace(encodingName) ? new UTF8Encoding(false) : Encoding.GetEncoding(encodingName);
            var settings = new XmlWriterSettings { Encoding = encoding, Indent = false, OmitXmlDeclaration = document.Declaration is null, NewLineHandling = NewLineHandling.None, Async = true };
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var writer = XmlWriter.Create(stream, settings);
                document.WriteTo(writer);
                await writer.FlushAsync();
            }

            var saved = Load(temporary);
            if (NonTextFingerprint(saved) != beforeFingerprint) throw new InvalidDataException("A validação detectou alteração em dados não textuais.");
            if (expectedTexts is not null)
            {
                var savedTexts = BuildTextMap(saved);
                if (savedTexts.Count != expectedTexts.Count || expectedTexts.Any(x => !savedTexts.TryGetValue(x.Key, out var value) || value.Value != x.Value.Value))
                    throw new InvalidDataException("A validação final dos textos falhou.");
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
