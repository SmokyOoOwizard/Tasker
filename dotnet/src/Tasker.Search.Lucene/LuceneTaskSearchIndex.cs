using System.Globalization;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Serilog;
using Tasker.Core.Tasks.Search;
using Directory = Lucene.Net.Store.Directory;
using LuceneDocument = Lucene.Net.Documents.Document;

namespace Tasker.Search.Lucene;

public sealed class LuceneTaskSearchIndex(string? path) : ITaskSearchIndex, IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;
    private const string IdField = "id";
    private const string ProjectField = "project";
    private const string StampField = "stamp";
    private const string NumberField = "number";
    private const string TitleField = "title";
    private const string DescriptionField = "description";
    private const int FuzzyExpansions = 200;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Dictionary<Guid, string>> _stamps = [];
    private Directory? _directory;
    private IndexWriter? _writer;
    private SearcherManager? _searchers;

    public async Task Update(
        Guid projectId,
        Func<IReadOnlyDictionary<Guid, string>, CancellationToken, Task<TaskSearchChanges>> changes,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Open();
            if (!_stamps.TryGetValue(projectId, out var known))
                _stamps[projectId] = known = [];

            var pending = await changes(known, ct);
            if (pending.IsEmpty)
                return;

            foreach (var document in pending.Upserts)
            {
                _writer!.UpdateDocument(new Term(IdField, Key(document.Id)), ToDocument(projectId, document));
                known[document.Id] = document.Stamp;
            }
            foreach (var id in pending.Removed)
            {
                _writer!.DeleteDocuments(new Term(IdField, Key(id)));
                known.Remove(id);
            }
            _writer!.Commit();
            _searchers!.MaybeRefreshBlocking();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Guid[]> Candidates(Guid projectId, TaskSearchQuery query, int limit, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        SearcherManager searchers;
        try
        {
            Open();
            searchers = _searchers!;
        }
        finally
        {
            _gate.Release();
        }

        var searcher = searchers.Acquire();
        try
        {
            var top = searcher.Search(BuildQuery(projectId, query), limit);
            var ids = new Guid[top.ScoreDocs.Length];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = Guid.Parse(searcher.Doc(top.ScoreDocs[i].Doc).Get(IdField));
            return ids;
        }
        finally
        {
            searchers.Release(searcher);
        }
    }

    public void Dispose()
    {
        _searchers?.Dispose();
        _writer?.Dispose();
        _directory?.Dispose();
        _gate.Dispose();
    }

    private void Open()
    {
        if (_writer != null)
            return;

        if (path != null)
            OpenFolder(path);
        if (_writer == null)
            Attach(new RAMDirectory());

        LoadStamps();
    }

    private void OpenFolder(string folder)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var directory = FSDirectory.Open(folder);
            try
            {
                Attach(directory);
                return;
            }
            catch (LockObtainFailedException)
            {
                directory.Dispose();
                Log.Warning("Search index in {Path} is used by another process: keeping this one in memory", folder);
                return;
            }
            catch (Exception e) when (e is IOException or IndexFormatTooOldException or IndexFormatTooNewException)
            {
                directory.Dispose();
                Log.Warning(e, "Search index in {Path} cannot be opened: building it again", folder);
                if (attempt == 0)
                    ClearFolder(folder);
            }
        }
    }

    private void Attach(Directory directory)
    {
        var analyzer = new WhitespaceAnalyzer(Version);
        var writer = new IndexWriter(directory, new IndexWriterConfig(Version, analyzer) { OpenMode = OpenMode.CREATE_OR_APPEND });
        _searchers = new SearcherManager(writer, true, null);
        _writer = writer;
        _directory = directory;
    }

    private static void ClearFolder(string folder)
    {
        try
        {
            if (System.IO.Directory.Exists(folder))
                System.IO.Directory.Delete(folder, recursive: true);
        }
        catch (IOException e)
        {
            Log.Warning(e, "Search index in {Path} cannot be removed", folder);
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Warning(e, "Search index in {Path} cannot be removed", folder);
        }
    }

    private void LoadStamps()
    {
        var searcher = _searchers!.Acquire();
        try
        {
            var reader = searcher.IndexReader;
            var live = MultiFields.GetLiveDocs(reader);
            for (var i = 0; i < reader.MaxDoc; i++)
            {
                if (live != null && !live.Get(i))
                    continue;

                var document = reader.Document(i);
                var project = Guid.Parse(document.Get(ProjectField));
                if (!_stamps.TryGetValue(project, out var known))
                    _stamps[project] = known = [];
                known[Guid.Parse(document.Get(IdField))] = document.Get(StampField);
            }
        }
        finally
        {
            _searchers.Release(searcher);
        }
    }

    private static LuceneDocument ToDocument(Guid projectId, TaskSearchDocument task)
    {
        var document = new LuceneDocument
        {
            new StringField(IdField, Key(task.Id), Field.Store.YES),
            new StringField(ProjectField, Key(projectId), Field.Store.YES),
            new StoredField(StampField, task.Stamp),
            new TextField(TitleField, TaskSearchText.IndexText(task.Title), Field.Store.NO),
            new TextField(DescriptionField, TaskSearchText.IndexText(task.Description), Field.Store.NO)
        };
        foreach (var number in task.Numbers)
            document.Add(new StringField(NumberField, number.ToString(CultureInfo.InvariantCulture), Field.Store.NO));
        return document;
    }

    private static Query BuildQuery(Guid projectId, TaskSearchQuery query)
    {
        var any = new BooleanQuery();
        if (query.Number is { } number)
        {
            any.Add(Boosted(new TermQuery(new Term(NumberField, number.Digits)), 200), Occur.SHOULD);
            any.Add(Boosted(new PrefixQuery(new Term(NumberField, number.Digits)), 100), Occur.SHOULD);
        }
        foreach (var words in query.WordSets)
        {
            if (words.Length == 0)
                continue;

            var all = new BooleanQuery();
            foreach (var word in words)
                all.Add(WordQuery(word), Occur.MUST);
            any.Add(all, Occur.SHOULD);
        }

        var result = new BooleanQuery
        {
            { new TermQuery(new Term(ProjectField, Key(projectId))), Occur.MUST },
            { any, Occur.MUST }
        };
        return result;
    }

    private static DisjunctionMaxQuery WordQuery(string word)
    {
        var edits = TaskSearchText.AllowedEdits(word);
        var clauses = new DisjunctionMaxQuery(0);
        AddField(clauses, TitleField, word, edits, 16);
        AddField(clauses, DescriptionField, word, edits, 2);
        return clauses;
    }

    private static void AddField(DisjunctionMaxQuery clauses, string field, string word, int edits, float weight)
    {
        var term = new Term(field, word);
        clauses.Add(Boosted(new TermQuery(term), weight * 4));
        clauses.Add(Boosted(new WildcardQuery(new Term(field, "*" + word + "*")), weight * 2));
        if (edits > 0)
            clauses.Add(Boosted(new FuzzyQuery(term, edits, 0, FuzzyExpansions, true), weight));
    }

    private static T Boosted<T>(T query, float boost) where T : Query
    {
        query.Boost = boost;
        return query;
    }

    private static string Key(Guid id) => id.ToString("D");
}
