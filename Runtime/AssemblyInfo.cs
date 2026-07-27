using System.Runtime.CompilerServices;

// Служебные поля ref-ассета (recordType, revision, domainName) ведёт пайплайн и никто больше —
// поэтому они internal, а не public: потребителю они не нужны и портить их он не должен.
[assembly: InternalsVisibleTo("Blobcheg.Authoring")]
[assembly: InternalsVisibleTo("Blobcheg.Tests")]
