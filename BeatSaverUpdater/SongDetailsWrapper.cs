using System.Threading.Tasks;
using SongDetailsCache;

namespace BeatSaverUpdater
{
    internal class SongDetailsWrapper
    {
        private class AntiBox
        {
            public readonly SongDetails instance;

            public AntiBox(SongDetails instance)
            {
                this.instance = instance;
            }
        }

        private AntiBox? songDetails;
        public async Task<bool> SongExists(string hash)
        {
            songDetails ??= new AntiBox(await SongDetails.Init());
            return songDetails.instance.songs.FindByHash(hash, out var song);
        }
    }
}
