using System.Runtime.CompilerServices;

// The bookkeeping fields of a ref asset (recordType, revision, domainName) are kept by the pipeline
// and by nobody else — which is why they are internal and not public: the consumer does not need them
// and must not spoil them.
[assembly: InternalsVisibleTo("Blobcheg.Authoring")]
[assembly: InternalsVisibleTo("Blobcheg.Tests")]

// The destructive set of the patch: it needs BlobchegBases.Clear (isolation of the registry between
// tests) and the bookkeeping fields of a ref asset assembled in memory instead of in the AssetDatabase.
[assembly: InternalsVisibleTo("Blobcheg.EntitiesPatch.Tests")]
