namespace ExcelClosedXML
{
    /// <summary>
    /// Point d'indirection pour le logging, utilisable depuis le projet partagé
    /// (qui ne cible pas net10.0-android et n'a donc pas accès à Android.Util.Log).
    /// La tête Android (MainActivity) redirige Sink vers Android.Util.Log.Debug
    /// au démarrage de l'app ; par défaut, les logs partent dans Debug.WriteLine
    /// (visible uniquement dans la fenêtre Sortie de Visual Studio).
    /// </summary>
    public static class AppLog
    {
        public static Action<string> Sink { get; set; } =
            msg => System.Diagnostics.Debug.WriteLine($"[SmbSave] {msg}");

        public static void Write(string message) => Sink(message);
    }
}
