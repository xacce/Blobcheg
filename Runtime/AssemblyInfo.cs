using System.Runtime.CompilerServices;

// Служебные поля ref-ассета (recordType, revision, domainName) ведёт пайплайн и никто больше —
// поэтому они internal, а не public: потребителю они не нужны и портить их он не должен.
[assembly: InternalsVisibleTo("Blobcheg.Authoring")]
[assembly: InternalsVisibleTo("Blobcheg.Tests")]

// Деструктивный набор патча: ему нужен BlobchegBases.Clear (изоляция реестра между тестами) и
// служебные поля ref-ассета, собираемого в памяти вместо AssetDatabase.
[assembly: InternalsVisibleTo("Blobcheg.EntitiesPatch.Tests")]
