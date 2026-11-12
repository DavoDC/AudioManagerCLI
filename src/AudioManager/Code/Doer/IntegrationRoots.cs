namespace AudioManager
{
    /// <summary>
    /// The four folders MusicIntegrator reads, fixed for one run. FromConstants() takes them from Constants,
    /// which already honours AUDIOMANAGER_TEST_ROOT, so a sandboxed run gets sandbox roots.
    /// </summary>
    internal sealed class IntegrationRoots
    {
        public IntegrationRoots(string newMusic, string library, string mirror, string logs)
        {
            NewMusic = newMusic;
            Library = library;
            Mirror = mirror;
            Logs = logs;
        }

        /// <summary>Staging folder (Downloads\NewMusic).</summary>
        public string NewMusic { get; }

        /// <summary>Audio library root.</summary>
        public string Library { get; }

        /// <summary>AudioMirror XML root.</summary>
        public string Mirror { get; }

        /// <summary>Logs folder (routing and execution records).</summary>
        public string Logs { get; }

        /// <summary>Roots taken from the Constants values in effect for this process.</summary>
        public static IntegrationRoots FromConstants() =>
            new IntegrationRoots(Constants.NewMusicPath, Constants.AudioFolderPath, Constants.MirrorFolderPath, Constants.LogsPath);
    }
}
