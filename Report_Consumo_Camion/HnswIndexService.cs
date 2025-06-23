using HNSW.Net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace CamionReportGPT.Vector
{
    public sealed class HnswIndexService
    {
        private const int DIM = 1536;
        private const int M = 16;
        private const int EF_CONSTRUCTION = 200;
        private const int EF_SEARCH = 80;

        // Il grafo HNSW vero e proprio
        private readonly SmallWorld<float[], float> _graph;
        private readonly List<float[]> _vectors = new();
        private readonly List<int> _ids = new();
        private readonly string _pathGraph;

        public HnswIndexService(string pathGraph)
        {
            _pathGraph = pathGraph;

            // 1) Preparo i parametri
            var parms = new SmallWorld<float[], float>.Parameters
            {
                M = M,
                LevelLambda = 1.0 / Math.Log(M),
                ConstructionPruning = EF_CONSTRUCTION
            };

            // 2) Costruisco l'istanza: (distanceFn, generator, parameters, threadSafe)
            _graph = new SmallWorld<float[], float>(
                CosineDistance.ForUnits,             // Func<float[],float[],float>
                DefaultRandomGenerator.Instance,     // IProvideRandomValues
                parms,                               // Parameters
                threadSafe: true                     // abilita lock
            );

            // 3) Se ho un file serializzato, ricarico vettori, id e grafo
            if (File.Exists(_pathGraph) && File.Exists(VectorsPath) && File.Exists(IdsPath))
            {
                _vectors.AddRange(LoadVectors(VectorsPath));
                _ids.AddRange(LoadIds(IdsPath));
                using var fs = File.OpenRead(_pathGraph);
                // firma: DeserializeGraph(items, distanceFn, generator, stream, threadSafe)
                var tuple = SmallWorld<float[], float>
                    .DeserializeGraph(
                        _vectors,
                        CosineDistance.ForUnits,
                        DefaultRandomGenerator.Instance,
                        fs,
                        threadSafe: true
                    );
                _graph = tuple.Graph;
            }
        }

        /// <summary>
        /// Aggiunge un vettore (normalizzato) all’indice in modo incrementale.
        /// </summary>
        public void Add(string id, float[] vector)
        {
            int dbId = int.Parse(id);
            var v = Normalize(vector);
            _ids.Add(dbId);
            _vectors.Add(v);
            _graph.AddItems(new[] { v });
        }

        /// <summary>
        /// Cerca i k-NN e restituisce (Id, ScoreCosine)
        /// </summary>
        public IList<(int Id, float Score)> Search(float[] query, int k = 3)
        {
            var results = _graph.KNNSearch(Normalize(query), k);
#if DEBUG
            if (results?.Count > 0)
                MessageBox.Show(string.Join("\n", results
                    .Take(k)
                    .Select((r, i) => $"Hit #{i + 1}: idx={r.Id}, score={(1 - r.Distance):F3}")),
                    $"DEBUG – HNSW Search (k={k})");
#endif

            if (results is null) return new List<(int, float)>();

            return results.Select(r => {
                int idx = r.Id;
                int realDbId = _ids[idx];
                float similarity = 1 - r.Distance;
                return (realDbId, similarity);
            }).ToList();
        }


        /// <summary>
        /// Salva il grafo e i vettori su disco.
        /// </summary>
        public void Save()
        {
            if (_vectors.Count == 0)
            {
                Debug.WriteLine("HNSW Save: nessun vettore da serializzare, salto.");
                return;                     // evita chiamate inutili durante OnClosing
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_pathGraph)!);

            using var fs = File.Create(_pathGraph);
            _graph.SerializeGraph(fs);

            SaveVectors(VectorsPath, _vectors);   // ← ora è sicuro
            SaveIds(IdsPath, _ids);
            MessageBox.Show($"Indice salvato in:\n{_pathGraph}\nVettori in:\n{VectorsPath}\nIds in:\n{IdsPath}",
                            "HNSW Save OK");
        }


        public static float[] Normalize(float[] v)
        {
            var norm = Math.Sqrt(v.Sum(x => x * x));
            if (norm == 0) return v;
            var inv = 1.0 / norm;
            return v.Select(x => (float)(x * inv)).ToArray();
        }

        private string VectorsPath => Path.ChangeExtension(_pathGraph, ".vec");

        private string IdsPath => Path.ChangeExtension(_pathGraph, ".ids");
        private static void SaveVectors(string path, IList<float[]> list)
        {
            if (list.Count == 0)            // ① exit-early se non c’è nulla da salvare
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!); // ② safe-dir
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(list.Count);
            bw.Write(list[0].Length);
            foreach (var vec in list)
                foreach (var f in vec)
                    bw.Write(f);
        }
        private static void SaveIds(string path, IList<int> list)
        {
            if (list.Count == 0)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(list.Count);
            foreach (var id in list)
                bw.Write(id);
        }
        private static List<float[]> LoadVectors(string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));
            int count = br.ReadInt32();
            int dim = br.ReadInt32();
            var list = new List<float[]>(count);
            for (int i = 0; i < count; i++)
            {
                var v = new float[dim];
                for (int j = 0; j < dim; j++)
                    v[j] = br.ReadSingle();
                list.Add(v);        
            }
            return list;
        }
        private static List<int> LoadIds(string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));
            int count = br.ReadInt32();
            var list = new List<int>(count);
            for (int i = 0; i < count; i++)
                list.Add(br.ReadInt32());
            return list;
        }
    }
}
