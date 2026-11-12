namespace AudioManager
{
    internal static class IntegrationRootsTests
    {
        // ---- IntegrationRoots.FromConstants ----

        public static void FromConstants_NewMusic_IsConstantsNewMusicPath()
        {
            Assert.Equal(Constants.NewMusicPath, IntegrationRoots.FromConstants().NewMusic, "NewMusic root is the NewMusic constant");
        }

        public static void FromConstants_Library_IsConstantsAudioFolderPath()
        {
            Assert.Equal(Constants.AudioFolderPath, IntegrationRoots.FromConstants().Library, "Library root is the Audio folder constant");
        }

        public static void FromConstants_Mirror_IsConstantsMirrorFolderPath()
        {
            Assert.Equal(Constants.MirrorFolderPath, IntegrationRoots.FromConstants().Mirror, "Mirror root is the mirror folder constant");
        }

        public static void FromConstants_Logs_IsConstantsLogsPath()
        {
            Assert.Equal(Constants.LogsPath, IntegrationRoots.FromConstants().Logs, "Logs root is the logs constant");
        }

        // ---- MusicIntegrator test constructor with injected roots ----

        public static void TestConstructor_StoresInjectedRootsObject()
        {
            var roots = new IntegrationRoots("nm", "lib", "mir", "logs");
            var integrator = new MusicIntegrator("lib", roots);
            Assert.True(ReferenceEquals(roots, integrator._roots), "test constructor keeps the injected roots object");
        }
    }
}
