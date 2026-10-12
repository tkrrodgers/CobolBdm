using System.Numerics.Tensors;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms;

namespace MlCpu.SemanticKb.Classify;

/// <summary>A training row: the feature vector (the 768-d embedding, optionally followed by structure features), the
/// label and the program it belongs to (the cross-validation group).</summary>
public sealed record LabeledExample(float[] Features, string Label, string Group);

public sealed record ClassMetrics(string Label, int Support, double Precision, double Recall, double F1);

/// <summary>Micro and macro accuracy are pooled over every held-out row (macro = mean per-class recall), so they match
/// the per-class table and the confusion matrix; ML.NET's own fold averages are kept alongside.</summary>
public sealed record ClassifierReport(
    string Family, string Trainer, string Features, int Examples, int Groups, int Folds, IReadOnlyDictionary<string, int> Classes,
    double MicroAccuracy, double MacroAccuracy, double LogLoss, double FoldMicroAccuracy, double FoldMacroAccuracy,
    double PrototypeMicroAccuracy, double PrototypeMacroAccuracy,
    IReadOnlyList<ClassMetrics> PerClass, IReadOnlyList<string> ConfusionLabels, int[][] Confusion, string LabelSource,
    IReadOnlyList<TrainerScore> Candidates);

public sealed record TrainerScore(string Trainer, double MicroAccuracy, double MacroAccuracy);

/// <summary>
/// One label family's ML.NET classifier (knowledgeBaseGroundGemma4.md §8): labels mapped to keys,
/// <c>SdcaMaximumEntropy</c> (L2-regularized multinomial logistic regression) with class-balancing example weights over
/// EmbeddingGemma vectors (plus structure features for <c>role</c>), evaluated by k-fold <c>CrossValidate</c> with the
/// program as the sampling key (the COBOL, Java and rule views of a paragraph never sit on both sides of a fold), and
/// compared with a zero-shot baseline: the class whose description embedding is nearest.
/// </summary>
public sealed class LabelClassifier : IDisposable
{
    const int EmbeddingDims = Embedding.EmbeddingGemma.Dimensions;
    readonly MLContext _ml;
    readonly ITransformer _model;
    readonly SchemaDefinition _schema;
    readonly PredictionEngine<Row, Prediction> _engine;
    readonly object _gate = new();

    public string Family { get; }
    public int Dims { get; }
    public IReadOnlyList<string> Classes { get; }
    public ClassifierReport Report { get; }

    LabelClassifier(string family, MLContext ml, ITransformer model, SchemaDefinition schema, int dims, IReadOnlyList<string> classes, ClassifierReport report)
    {
        Family = family; _ml = ml; _model = model; _schema = schema; Dims = dims; Classes = classes; Report = report;
        _engine = ml.Model.CreatePredictionEngine<Row, Prediction>(model, inputSchemaDefinition: schema);
    }

    static SchemaDefinition Schema(int dims)
    {
        var s = SchemaDefinition.Create(typeof(Row));
        s["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, dims);
        return s;
    }

    public static LabelClassifier? Train(string family, string labelSource, string featureNote, IReadOnlyList<LabeledExample> examples,
        IReadOnlyDictionary<string, float[]> prototypes, IReadOnlyList<string> trainers, int folds = 5, int seed = 1)
    {
        var classCounts = examples.GroupBy(e => e.Label).ToDictionary(g => g.Key, g => g.Count());
        if (classCounts.Count < 2 || examples.Count < 20) return null;
        int dims = examples[0].Features.Length;
        var schema = Schema(dims);
        var ml = new MLContext(seed);
        int n = examples.Count, c = classCounts.Count;
        var rows = examples.Select(e => new Row
        {
            Features = e.Features,
            Label = e.Label,
            Group = e.Group,
            Weight = (float)Math.Clamp((double)n / (c * classCounts[e.Label]), 0.2, 5)
        }).ToList();
        var data = ml.Data.LoadFromEnumerable(rows, schema);
        var toKey = ml.Transforms.Conversion.MapValueToKey("Label", "Label", keyOrdinality: ValueToKeyMappingEstimator.KeyOrdinality.ByValue).Fit(data);
        var keyed = toKey.Transform(data);
        VBuffer<ReadOnlyMemory<char>> names = default;
        keyed.Schema["Label"].GetKeyValues(ref names);
        var classes = names.DenseValues().Select(m => m.ToString()).ToList();

        IEstimator<ITransformer> Make(string name) => name switch
        {
            "lightgbm" => ml.MulticlassClassification.Trainers.LightGbm(new Microsoft.ML.Trainers.LightGbm.LightGbmMulticlassTrainer.Options
            {
                LabelColumnName = "Label", FeatureColumnName = "Features", ExampleWeightColumnName = "Weight",
                NumberOfIterations = 150, LearningRate = 0.08, NumberOfLeaves = 15, MinimumExampleCountPerLeaf = 10,
                Seed = seed, NumberOfThreads = 1, Verbose = false, Silent = true
            }),
            _ => ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(new Microsoft.ML.Trainers.SdcaMaximumEntropyMulticlassTrainer.Options
            {
                LabelColumnName = "Label", FeatureColumnName = "Features", ExampleWeightColumnName = "Weight",
                MaximumNumberOfIterations = 200, NumberOfThreads = 1
            })
        };

        // Cross-validation grouped by program, pooled confusion over all held-out rows, one run per candidate trainer;
        // the candidate with the better mean of pooled micro and macro accuracy is deployed.
        int groups = examples.Select(e => e.Group).Distinct().Count();
        int k = Math.Max(2, Math.Min(folds, groups));
        var candidates = new List<TrainerScore>();
        string trainerName = trainers[0];
        int[][] confusion = Square(classes.Count);
        IReadOnlyList<TrainCatalogBase.CrossValidationResult<MulticlassClassificationMetrics>> cv = [];
        double bestScore = double.MinValue;
        foreach (var name in trainers)
        {
            var run = ml.MulticlassClassification.CrossValidate(keyed, Make(name), numberOfFolds: k, labelColumnName: "Label", samplingKeyColumnName: "Group", seed: seed);
            var m = Square(classes.Count);
            foreach (var fold in run)
            {
                var truth = fold.ScoredHoldOutSet.GetColumn<uint>("Label").ToArray();
                var pred = fold.ScoredHoldOutSet.GetColumn<uint>("PredictedLabel").ToArray();
                for (int i = 0; i < truth.Length; i++)
                    if (truth[i] > 0 && pred[i] > 0) m[truth[i] - 1][pred[i] - 1]++;
            }
            var score = new TrainerScore(name, Math.Round(Micro(m), 4), Math.Round(PerClass(classes, m).Average(p => p.Recall), 4));
            candidates.Add(score);
            if (score.MicroAccuracy + score.MacroAccuracy > bestScore)
            {
                bestScore = score.MicroAccuracy + score.MacroAccuracy;
                (trainerName, confusion, cv) = (name, m, run);
            }
        }
        var perClass = PerClass(classes, confusion);

        // Zero-shot baseline over the embedding part only: the class whose prototype is nearest.
        var protoConfusion = Square(classes.Count);
        var protos = classes.Select(cl => prototypes.GetValueOrDefault(cl)).ToList();
        if (protos.All(p => p is not null))
            foreach (var e in examples)
            {
                int best = 0; float nearest = float.MinValue;
                var emb = e.Features.AsSpan(0, EmbeddingDims);
                for (int j = 0; j < protos.Count; j++)
                {
                    var s = TensorPrimitives.Dot(emb, protos[j]!);
                    if (s > nearest) { nearest = s; best = j; }
                }
                protoConfusion[classes.IndexOf(e.Label)][best]++;
            }
        var protoPer = PerClass(classes, protoConfusion);

        // The deployed model: the same key mapping (by value, so the classes match the CV's) + trainer + key → label.
        var model = ml.Transforms.Conversion.MapValueToKey("Label", "Label", keyOrdinality: ValueToKeyMappingEstimator.KeyOrdinality.ByValue)
            .Append(Make(trainerName))
            .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"))
            .Fit(data);
        var trainerTitle = trainerName == "lightgbm" ? "ML.NET LightGbm multiclass (class-balanced weights)" : "ML.NET SdcaMaximumEntropy (class-balanced weights)";
        var report = new ClassifierReport(family, trainerTitle, featureNote, n, groups, k,
            classes.ToDictionary(cl => cl, cl => classCounts[cl]),
            Math.Round(Micro(confusion), 4), Math.Round(perClass.Average(p => p.Recall), 4), Math.Round(cv.Average(f => f.Metrics.LogLoss), 4),
            Math.Round(cv.Average(f => f.Metrics.MicroAccuracy), 4), Math.Round(cv.Average(f => f.Metrics.MacroAccuracy), 4),
            Math.Round(Micro(protoConfusion), 4), Math.Round(protoPer.Average(p => p.Recall), 4),
            perClass, classes, confusion, labelSource, candidates);
        return new LabelClassifier(family, ml, model, schema, dims, classes, report);
    }

    /// <summary>The predicted class and its probability.</summary>
    public (string Label, double Probability) Predict(float[] features)
    {
        lock (_gate)
        {
            var p = _engine.Predict(new Row { Features = features, Label = "", Group = "", Weight = 1 });
            return (p.PredictedLabel, p.Score.Length == 0 ? 0 : Math.Round(p.Score.Max(), 4));
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ml.Model.Save(_model, _ml.Data.LoadFromEnumerable(new List<Row>(), _schema).Schema, path);
    }

    static int[][] Square(int n) => Enumerable.Range(0, n).Select(_ => new int[n]).ToArray();

    static List<ClassMetrics> PerClass(IReadOnlyList<string> classes, int[][] m)
    {
        var list = new List<ClassMetrics>();
        for (int i = 0; i < classes.Count; i++)
        {
            int tp = m[i][i], support = m[i].Sum(), predicted = m.Sum(r => r[i]);
            double p = predicted == 0 ? 0 : (double)tp / predicted, r = support == 0 ? 0 : (double)tp / support;
            list.Add(new ClassMetrics(classes[i], support, Math.Round(p, 3), Math.Round(r, 3), p + r == 0 ? 0 : Math.Round(2 * p * r / (p + r), 3)));
        }
        return list;
    }

    static double Micro(int[][] m)
    {
        long total = m.Sum(r => (long)r.Sum());
        return total == 0 ? 0 : (double)Enumerable.Range(0, m.Length).Sum(i => m[i][i]) / total;
    }

    public void Dispose() => _engine.Dispose();

    sealed class Row
    {
        public float[] Features { get; set; } = [];
        public string Label { get; set; } = "";
        public string Group { get; set; } = "";
        public float Weight { get; set; }
    }

    sealed class Prediction
    {
        public string PredictedLabel { get; set; } = "";
        public float[] Score { get; set; } = [];
    }
}
