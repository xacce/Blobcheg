namespace Blobcheg.Authoring
{
    // The last caller is the import watcher of rift_unity; it goes away with that watcher.
    public static class BlobchegHooks
    {
        public static void MarkDirty() => BlobchegFreshness.Invalidate();
    }
}
