namespace SancMeld.Experiments.NameEmbeddings;

/// <summary>
/// Every text this model has embedded so far, on disk.
///
/// Embedding 20 000 names on a CPU is minutes to tens of minutes, and the second run of
/// the experiment — a different k, a different target list, a fix in the report — would
/// otherwise pay it again. The key is the exact text sent, prefix included, so a changed
/// prefix is a different entry rather than a stale one.
/// </summary>
public sealed class EmbeddingCache
{
    private const string Magic = "SMEMB1";

    private readonly string _path;
    private readonly Dictionary<string, float[]> _entries = new(StringComparer.Ordinal);
    private int _unsaved;

    private EmbeddingCache(string path) => _path = path;

    public int Count => _entries.Count;

    public static EmbeddingCache Load(string directory, string model)
    {
        Directory.CreateDirectory(directory);

        // Model names carry slashes and colons ("zylonai/multilingual-e5-large:latest").
        var safe = string.Concat(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var cache = new EmbeddingCache(Path.Combine(directory, safe + ".bin"));

        if (!File.Exists(cache._path))
        {
            return cache;
        }

        using var reader = new BinaryReader(File.OpenRead(cache._path));

        if (reader.ReadString() != Magic)
        {
            throw new InvalidDataException($"{cache._path} is not an embedding cache; delete it.");
        }

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var text = reader.ReadString();
            var dims = reader.ReadInt32();
            var vector = new float[dims];
            for (var d = 0; d < dims; d++)
            {
                vector[d] = reader.ReadSingle();
            }

            cache._entries[text] = vector;
        }

        return cache;
    }

    public bool TryGet(string text, out float[] vector) => _entries.TryGetValue(text, out vector!);

    public void Add(string text, float[] vector)
    {
        _entries[text] = vector;
        _unsaved++;
    }

    /// <summary>Written whole; appending would need a format that survives a crash mid-write.</summary>
    public void Save()
    {
        if (_unsaved == 0)
        {
            return;
        }

        var temp = _path + ".tmp";

        using (var writer = new BinaryWriter(File.Create(temp)))
        {
            writer.Write(Magic);
            writer.Write(_entries.Count);

            foreach (var (text, vector) in _entries)
            {
                writer.Write(text);
                writer.Write(vector.Length);
                foreach (var value in vector)
                {
                    writer.Write(value);
                }
            }
        }

        File.Move(temp, _path, overwrite: true);
        _unsaved = 0;
    }
}
