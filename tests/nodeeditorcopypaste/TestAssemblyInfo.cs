using Xunit;

// WzFile save/load goes through MapleLib's static crypto state (UserKey/IV tables); two test
// classes doing real file round-trips on parallel STA threads corrupt each other. Serial is
// correct here, not just convenient.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
