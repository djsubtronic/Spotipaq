// Backend.cs - everything the UI needs from "Spotify". The UI only ever talks to these two interfaces,
// so the mock can be swapped for the real (librespot-java-derived) engine without touching any page.
//
// Threading: ISpotifyBackend calls are blocking and are always made on a worker thread (see Async).
// IPlayback calls are made on the UI thread and must return quickly; its events may fire on any thread.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Spotipaq
{
    public interface ISpotifyBackend
    {
        UserInfo GetUser();
        List<Playlist> GetPlaylists();              // sidebar rootlist (tracks may be empty)
        List<BrowseSection> GetBrowse();
        Playlist GetPlaylist(string id);
        Album GetAlbum(string id);
        ArtistPage GetArtist(string id);
        SearchResults Search(string query);
        List<Track> GetSavedTracks();
        List<Album> GetSavedAlbums();
        List<Artist> GetFollowedArtists();
        /// <summary>Downloads/creates an image. Called on a worker thread; may return null.</summary>
        Bitmap LoadImage(string url);
        /// <summary>Spotify's "song radio" for a track, as a playlist id (null if not available).</summary>
        string GetSongRadio(string trackId);
        /// <summary>Saves (true) or removes (false) a track / album / artist / playlist (by spotify: uri) in the user's library.</summary>
        void SetSaved(string uri, bool saved);
    }

    public enum PlayState { Stopped, Loading, Playing, Paused }

    public interface IPlayback
    {
        event EventHandler StateChanged;
        event EventHandler TrackEnded;              // natural end: the queue moves on
        event EventHandler TrackUnplayable;         // the track just requested can't be played at all (after StateChanged -> Stopped)
        PlayState State { get; }
        Track Current { get; }
        int PositionMs { get; }
        int Volume { get; set; }                    // 0..100
        void Play(Track track, int startMs);
        void Pause();
        void Resume();
        void Seek(int ms);
        void Stop();
    }

    public delegate T Work<T>();
    public delegate void Done<T>(T result, Exception error);

    /// <summary>Run blocking backend work off the UI thread; deliver the result back on it.</summary>
    public static class Async
    {
        public static void Run<T>(Control owner, Work<T> work, Done<T> done)
        {
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                T result = default(T);
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                try
                {
                    if (owner.IsHandleCreated && !owner.IsDisposed)
                        owner.BeginInvoke(new MethodInvoker(delegate () { done(result, error); }));
                }
                catch (InvalidOperationException) { }      // form closing
            });
        }
    }
}
