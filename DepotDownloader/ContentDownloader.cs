using SteamKit2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    public static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "Public";

        public static DownloadConfig Config = new DownloadConfig();

        public delegate void LogStuff(string m);

        public static LogStuff WriteToLog = Console.WriteLine;

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string CONFIG_DIR = ".DepotDownloader";
        private static readonly string STAGING_DIR = Path.Combine( CONFIG_DIR, "staging" );

        private sealed class DepotDownloadInfo
        {
            public uint id { get; private set; }
            public string installDir { get; private set; }
            public string contentName { get; private set; }

            public ulong manifestId { get; private set; }
            public byte[] depotKey;

            public DepotDownloadInfo( uint depotid, ulong manifestId, string installDir, string contentName )
            {
                this.id = depotid;
                this.manifestId = manifestId;
                this.installDir = installDir;
                this.contentName = contentName;
            }
        }

        static bool CreateDirectories( uint depotId, uint depotVersion, out string installDir )
        {
            installDir = null;
            try
            {
                if ( string.IsNullOrWhiteSpace( ContentDownloader.Config.InstallDirectory ) )
                {
                    Directory.CreateDirectory( DEFAULT_DOWNLOAD_DIR );

                    string depotPath = Path.Combine( DEFAULT_DOWNLOAD_DIR, depotId.ToString() );
                    Directory.CreateDirectory( depotPath );

                    installDir = Path.Combine( depotPath, depotVersion.ToString() );
                    Directory.CreateDirectory( installDir );

                    Directory.CreateDirectory( Path.Combine( installDir, CONFIG_DIR ) );
                    Directory.CreateDirectory( Path.Combine( installDir, STAGING_DIR ) );
                }
                else
                {
                    Directory.CreateDirectory( ContentDownloader.Config.InstallDirectory );

                    installDir = ContentDownloader.Config.InstallDirectory;

                    Directory.CreateDirectory( Path.Combine( installDir, CONFIG_DIR ) );
                    Directory.CreateDirectory( Path.Combine( installDir, STAGING_DIR ) );
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        static bool TestIsFileIncluded( string filename )
        {
            if ( !Config.UsingFileList )
                return true;

            foreach ( string fileListEntry in Config.FilesToDownload )
            {
                if ( fileListEntry.Equals( filename, StringComparison.OrdinalIgnoreCase ) )
                    return true;
            }

            foreach ( Regex rgx in Config.FilesToDownloadRegex )
            {
                Match m = rgx.Match( filename );

                if ( m.Success )
                    return true;
            }

            return false;
        }

        static bool AccountHasAccess( uint depotId )
        {
            if ( steam3 == null || steam3.steamUser.SteamID == null || ( steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser ) )
                return false;

            IEnumerable<uint> licenseQuery;
            if ( steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser )
            {
                licenseQuery = new List<uint>() { 17906 };
            }
            else
            {
                licenseQuery = steam3.Licenses.Select( x => x.PackageID );
            }

            steam3.RequestPackageInfo( licenseQuery );

            foreach ( var license in licenseQuery )
            {
                SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                if ( steam3.PackageInfo.TryGetValue( license, out package ) && package != null )
                {
                    if ( package.KeyValues[ "appids" ].Children.Any( child => child.AsUnsignedInteger() == depotId ) )
                        return true;

                    if ( package.KeyValues[ "depotids" ].Children.Any( child => child.AsUnsignedInteger() == depotId ) )
                        return true;
                }
            }

            return false;
        }

        internal static KeyValue GetSteam3AppSection( uint appId, EAppInfoSection section )
        {
            if ( steam3 == null || steam3.AppInfo == null )
            {
                return null;
            }

            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
            if ( !steam3.AppInfo.TryGetValue( appId, out app ) || app == null )
            {
                return null;
            }

            KeyValue appinfo = app.KeyValues;
            string section_key;

            switch ( section )
            {
                case EAppInfoSection.Common:
                    section_key = "common";
                    break;
                case EAppInfoSection.Extended:
                    section_key = "extended";
                    break;
                case EAppInfoSection.Config:
                    section_key = "config";
                    break;
                case EAppInfoSection.Depots:
                    section_key = "depots";
                    break;
                default:
                    throw new NotImplementedException();
            }

            KeyValue section_kv = appinfo.Children.Where( c => c.Name == section_key ).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber( uint appId, string branch )
        {
            if ( appId == INVALID_APP_ID )
                return 0;


            KeyValue depots = ContentDownloader.GetSteam3AppSection( appId, EAppInfoSection.Depots );
            KeyValue branches = depots[ "branches" ];
            KeyValue node = branches[ branch ];

            if ( node == KeyValue.Invalid )
                return 0;

            KeyValue buildid = node[ "buildid" ];

            if ( buildid == KeyValue.Invalid )
                return 0;

            return uint.Parse( buildid.Value );
        }

        static ulong GetSteam3DepotManifest( uint depotId, uint appId, string branch )
        {
            KeyValue depots = GetSteam3AppSection( appId, EAppInfoSection.Depots );
            KeyValue depotChild = depots[ depotId.ToString() ];

            if ( depotChild == KeyValue.Invalid )
                return INVALID_MANIFEST_ID;

            // Shared depots can either provide manifests, or leave you relying on their parent app.
            // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
            // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
            if ( depotChild[ "manifests" ] == KeyValue.Invalid && depotChild[ "depotfromapp" ] != KeyValue.Invalid )
            {
                uint otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if ( otherAppId == appId )
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    WriteToLog($"App {appId}, Depot {depotId} has depotfromapp of {otherAppId}!");
                    return INVALID_MANIFEST_ID;
                }

                steam3.RequestAppInfo( otherAppId );

                return GetSteam3DepotManifest( depotId, otherAppId, branch );
            }

            var manifests = depotChild[ "manifests" ];
            var manifests_encrypted = depotChild[ "encryptedmanifests" ];

            if ( manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0 )
                return INVALID_MANIFEST_ID;

            var node = manifests[ branch ];

            if ( branch != "Public" && node == KeyValue.Invalid )
            {
                var node_encrypted = manifests_encrypted[ branch ];
                if ( node_encrypted != KeyValue.Invalid )
                {
                    string password = Config.BetaPassword;
                    if ( password == null )
                    {
                        Console.Write( "Please enter the password for branch {0}: ", branch );
                        Config.BetaPassword = password = Console.ReadLine();
                    }

                    var encrypted_v1 = node_encrypted[ "encrypted_gid" ];
                    var encrypted_v2 = node_encrypted[ "encrypted_gid_2" ];

                    if ( encrypted_v1 != KeyValue.Invalid )
                    {
                        byte[] input = Util.DecodeHexString( encrypted_v1.Value );
                        byte[] manifest_bytes = CryptoHelper.VerifyAndDecryptPassword( input, password );

                        if ( manifest_bytes == null )
                        {
                            WriteToLog($"Password was invalid for branch {branch}");
                            return INVALID_MANIFEST_ID;
                        }

                        return BitConverter.ToUInt64( manifest_bytes, 0 );
                    }
                    else if ( encrypted_v2 != KeyValue.Invalid )
                    {
                        // Submit the password to Steam now to get encryption keys
                        steam3.CheckAppBetaPassword( appId, Config.BetaPassword );

                        if ( !steam3.AppBetaPasswords.ContainsKey( branch ) )
                        {
                            WriteToLog($"Password was invalid for branch {branch}");
                            return INVALID_MANIFEST_ID;
                        }

                        byte[] input = Util.DecodeHexString( encrypted_v2.Value );
                        byte[] manifest_bytes;
                        try
                        {
                            manifest_bytes = CryptoHelper.SymmetricDecryptECB( input, steam3.AppBetaPasswords[ branch ] );
                        }
                        catch ( Exception e )
                        {
                            WriteToLog($"Failed to decrypt branch {branch}: {e.Message}");
                            return INVALID_MANIFEST_ID;
                        }

                        return BitConverter.ToUInt64( manifest_bytes, 0 );
                    }
                    else
                    {
                        WriteToLog($"Unhandled depot encryption for depotId {depotId}");
                        return INVALID_MANIFEST_ID;
                    }

                }

                return INVALID_MANIFEST_ID;
            }

            if ( node.Value == null )
                return INVALID_MANIFEST_ID;

            return UInt64.Parse( node.Value );
        }

        static string GetAppOrDepotName( uint depotId, uint appId )
        {
            if ( depotId == INVALID_DEPOT_ID )
            {
                KeyValue info = GetSteam3AppSection( appId, EAppInfoSection.Common );

                if ( info == null )
                    return String.Empty;

                return info[ "name" ].AsString();
            }
            else
            {
                KeyValue depots = GetSteam3AppSection( appId, EAppInfoSection.Depots );

                if ( depots == null )
                    return String.Empty;

                KeyValue depotChild = depots[ depotId.ToString() ];

                if ( depotChild == null )
                    return String.Empty;

                return depotChild[ "name" ].AsString();
            }
        }

        public static bool InitializeSteam3( string username, string password, Steam3Session.GetSteamGuardCode sgc, LogStuff logger )
        {
            string loginKey = null;

            if ( username != null && Config.RememberPassword )
            {
                _ = ConfigStore.TheConfig.LoginKeys.TryGetValue( username, out loginKey );
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = loginKey == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    LoginKey = loginKey,
                }, logger
            );

            steam3.GetSteamGC = sgc;

            steam3Credentials = steam3.WaitForCredentials();

            if ( !steam3Credentials.IsValid )
            {
                WriteToLog("Unable to get steam3 credentials.");
                return false;
            }

            cdnPool = new CDNClientPool( steam3 );
            return true;
        }

        public static void ShutdownSteam3()
        {
            if (cdnPool != null)
            {
                cdnPool.Shutdown();
                cdnPool = null;
            }

            if ( steam3 == null )
                return;

            steam3.TryWaitForLoginKey();
            steam3.Disconnect();
        }

        public static async Task DownloadPubfileAsync( ulong publishedFileId )
        {
            var details = steam3.GetPubfileDetails( publishedFileId );

            if ( details.hcontent_file > 0 )
            {
                await DownloadAppAsync( details.consumer_appid, details.consumer_appid, details.hcontent_file, DEFAULT_BRANCH, null, true );
            }
            else
            {
                WriteToLog($"Unable to locate manifest ID for published file {publishedFileId}");
            }
        }

        public static async Task DownloadAppAsync( uint appId, uint depotId, ulong manifestId, string branch, string os, bool isUgc )
        {
            if ( steam3 != null )
                steam3.RequestAppInfo( appId );

            if ( !AccountHasAccess( appId ) )
            {
                if ( steam3.RequestFreeAppLicense( appId ) )
                {
                    WriteToLog($"Obtained FreeOnDemand license for app {appId}");
                }
                else
                {
                    string contentName = GetAppOrDepotName( INVALID_DEPOT_ID, appId );
                    WriteToLog($"App {appId} ({contentName}) is not available from this account.");
                    return;
                }
            }

            var depotIDs = new List<uint>();
            KeyValue depots = GetSteam3AppSection( appId, EAppInfoSection.Depots );

            if ( isUgc )
            {
                var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
                if (workshopDepot != 0)
                    depotId = workshopDepot;

                depotIDs.Add( depotId );
            }
            else
            {
                WriteToLog($"Using app branch: '{branch}'.");

                if ( depots != null )
                {
                    foreach ( var depotSection in depots.Children )
                    {
                        uint id = INVALID_DEPOT_ID;
                        if ( depotSection.Children.Count == 0 )
                            continue;

                        if ( !uint.TryParse( depotSection.Name, out id ) )
                            continue;

                        if ( depotId != INVALID_DEPOT_ID && id != depotId )
                            continue;

                        if ( depotId == INVALID_DEPOT_ID && !Config.DownloadAllPlatforms )
                        {
                            var depotConfig = depotSection[ "config" ];
                            if ( depotConfig != KeyValue.Invalid && depotConfig[ "oslist" ] != KeyValue.Invalid && !string.IsNullOrWhiteSpace( depotConfig[ "oslist" ].Value ) )
                            {
                                var oslist = depotConfig[ "oslist" ].Value.Split( ',' );
                                if ( Array.IndexOf( oslist, os ?? Util.GetSteamOS() ) == -1 )
                                    continue;
                            }
                        }

                        depotIDs.Add( id );
                    }
                }
                if ( depotIDs == null || ( depotIDs.Count == 0 && depotId == INVALID_DEPOT_ID ) )
                {
                    WriteToLog($"Couldn't find any depots to download for app {appId}");
                    return;
                }
                else if ( depotIDs.Count == 0 )
                {
                    WriteToLog($"Depot {depotId} not listed for app {appId}");
                    return;
                }
            }

            var infos = new List<DepotDownloadInfo>();

            foreach ( var depot in depotIDs )
            {
                var info = GetDepotInfo( depot, appId, manifestId, branch );
                if ( info != null )
                {
                    infos.Add( info );
                }
            }

            try
            {
                await DownloadSteam3Async( appId, infos ).ConfigureAwait( false );
            }
            catch ( OperationCanceledException )
            {
                WriteToLog($"App {appId} was not completely downloaded.");
            }
        }

        static DepotDownloadInfo GetDepotInfo( uint depotId, uint appId, ulong manifestId, string branch )
        {
            if ( steam3 != null && appId != INVALID_APP_ID )
                steam3.RequestAppInfo( ( uint )appId );

            string contentName = GetAppOrDepotName( depotId, appId );

            if ( !AccountHasAccess( depotId ) )
            {
                WriteToLog($"Depot {depotId} ({contentName}) is not available from this account.");

                return null;
            }

            // Skip requesting an app ticket
            steam3.AppTickets[ depotId ] = null;

            if (manifestId == INVALID_MANIFEST_ID)
            {
                manifestId = GetSteam3DepotManifest(depotId, appId, branch);
                if (manifestId == INVALID_MANIFEST_ID && branch != "public")
                {
                    WriteToLog(
                        $"Warning: Depot {depotId} does not have branch named \"{branch}\". Trying public branch.");
                    branch = "public";
                    manifestId = GetSteam3DepotManifest(depotId, appId, branch);
                }

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    WriteToLog($"Depot {depotId} ({contentName}) missing public subsection or manifest section.");
                    return null;
                }
            }

            uint uVersion = GetSteam3AppBuildNumber( appId, branch );

            string installDir;
            if ( !CreateDirectories( depotId, uVersion, out installDir ) )
            {
                WriteToLog( "Error: Unable to create install directories!" );
                return null;
            }

            steam3.RequestDepotKey( depotId, appId );
            if ( !steam3.DepotKeys.ContainsKey( depotId ) )
            {
                WriteToLog($"No valid depot key for {depotId}, unable to download.");
                return null;
            }

            byte[] depotKey = steam3.DepotKeys[ depotId ];

            var info = new DepotDownloadInfo( depotId, manifestId, installDir, contentName );
            info.depotKey = depotKey;
            return info;
        }

        private class ChunkMatch
        {
            public ChunkMatch( ProtoManifest.ChunkData oldChunk, ProtoManifest.ChunkData newChunk )
            {
                OldChunk = oldChunk;
                NewChunk = newChunk;
            }
            public ProtoManifest.ChunkData OldChunk { get; private set; }
            public ProtoManifest.ChunkData NewChunk { get; private set; }
        }

        private static async Task DownloadSteam3Async( uint appId, List<DepotDownloadInfo> depots )
        {
            ulong TotalBytesCompressed = 0;
            ulong TotalBytesUncompressed = 0;

            foreach ( var depot in depots )
            {
                ulong DepotBytesCompressed = 0;
                ulong DepotBytesUncompressed = 0;

                WriteToLog($"Downloading depot {depot.id} - {depot.contentName}");

                CancellationTokenSource cts = new CancellationTokenSource();
                cdnPool.ExhaustedToken = cts;

                ProtoManifest oldProtoManifest = null;
                ProtoManifest newProtoManifest = null;
                string configDir = Path.Combine( depot.installDir, CONFIG_DIR );

                ulong lastManifestId = INVALID_MANIFEST_ID;
                ConfigStore.TheConfig.LastManifests.TryGetValue( depot.id, out lastManifestId );

                // In case we have an early exit, this will force equiv of verifyall next run.
                ConfigStore.TheConfig.LastManifests[ depot.id ] = INVALID_MANIFEST_ID;
                ConfigStore.Save();

                if ( lastManifestId != INVALID_MANIFEST_ID )
                {
                    var oldManifestFileName = Path.Combine( configDir, $"{lastManifestId}.bin");
                    if ( File.Exists( oldManifestFileName ) )
                        oldProtoManifest = ProtoManifest.LoadFromFile( oldManifestFileName );
                }

                if ( lastManifestId == depot.manifestId && oldProtoManifest != null )
                {
                    newProtoManifest = oldProtoManifest;
                    WriteToLog($"Already have manifest {depot.manifestId} for depot {depot.id}.");
                }
                else
                {
                    var newManifestFileName = Path.Combine( configDir, $"{depot.manifestId}.bin");
                    if ( newManifestFileName != null )
                    {
                        newProtoManifest = ProtoManifest.LoadFromFile( newManifestFileName );
                    }

                    if ( newProtoManifest != null )
                    {
                        WriteToLog($"Already have manifest {depot.manifestId} for depot {depot.id}.");
                    }
                    else
                    {
                        WriteToLog( "Downloading depot manifest..." );

                        DepotManifest depotManifest = null;

                        while ( depotManifest == null )
                        {
                            CDNClient client = null;
                            try
                            {
                                client = await cdnPool.GetConnectionForDepotAsync( appId, depot.id, depot.depotKey, CancellationToken.None ).ConfigureAwait( false );

                                depotManifest = await client.DownloadManifestAsync( depot.id, depot.manifestId ).ConfigureAwait( false );

                                cdnPool.ReturnConnection( client );
                            }
                            catch ( WebException e )
                            {
                                cdnPool.ReturnBrokenConnection( client );

                                if ( e.Status == WebExceptionStatus.ProtocolError )
                                {
                                    var response = e.Response as HttpWebResponse;
                                    if ( response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden )
                                    {
                                        WriteToLog(
                                            $"Encountered 401 for depot manifest {depot.id} {depot.manifestId}. Aborting.");
                                        break;
                                    }
                                    else
                                    {
                                        WriteToLog(
                                            $"Encountered error downloading depot manifest {depot.id} {depot.manifestId}: {response.StatusCode}");
                                    }
                                }
                                else
                                {
                                    WriteToLog(
                                        $"Encountered error downloading manifest for depot {depot.id} {depot.manifestId}: {e.Status}");
                                }
                            }
                            catch ( Exception e )
                            {
                                cdnPool.ReturnBrokenConnection( client );
                                WriteToLog(
                                    $"Encountered error downloading manifest for depot {depot.id} {depot.manifestId}: {e.Message}");
                            }
                        }

                        if ( depotManifest == null )
                        {
                            WriteToLog($"\nUnable to download manifest {depot.manifestId} for depot {depot.id}");
                            return;
                        }

                        newProtoManifest = new ProtoManifest( depotManifest, depot.manifestId );
                        newProtoManifest.SaveToFile( newManifestFileName );

                        WriteToLog( " Done!" );
                    }
                }

                newProtoManifest.Files.Sort( ( x, y ) => { return x.FileName.CompareTo( y.FileName ); } );

                if ( Config.DownloadManifestOnly )
                {
                    StringBuilder manifestBuilder = new StringBuilder();
                    string txtManifest = Path.Combine( depot.installDir, $"manifest_{depot.id}.txt");

                    foreach ( var file in newProtoManifest.Files )
                    {
                        if ( file.Flags.HasFlag( EDepotFileFlag.Directory ) )
                            continue;

                        manifestBuilder.Append($"{file.FileName}\n");
                    }

                    File.WriteAllText( txtManifest, manifestBuilder.ToString() );
                    continue;
                }

                ulong complete_download_size = 0;
                ulong size_downloaded = 0;
                string stagingDir = Path.Combine( depot.installDir, STAGING_DIR );

                var filesAfterExclusions = newProtoManifest.Files.AsParallel().Where( f => TestIsFileIncluded( f.FileName ) ).ToList();

                // Pre-process
                filesAfterExclusions.ForEach( file =>
                {
                    var fileFinalPath = Path.Combine( depot.installDir, file.FileName );
                    var fileStagingPath = Path.Combine( stagingDir, file.FileName );

                    if ( file.Flags.HasFlag( EDepotFileFlag.Directory ) )
                    {
                        Directory.CreateDirectory( fileFinalPath );
                        Directory.CreateDirectory( fileStagingPath );
                    }
                    else
                    {
                        // Some manifests don't explicitly include all necessary directories
                        Directory.CreateDirectory( Path.GetDirectoryName( fileFinalPath ) );
                        Directory.CreateDirectory( Path.GetDirectoryName( fileStagingPath ) );

                        complete_download_size += file.TotalSize;
                    }
                } );

                var semaphore = new SemaphoreSlim( Config.MaxDownloads );
                var files = filesAfterExclusions.Where( f => !f.Flags.HasFlag( EDepotFileFlag.Directory ) ).ToArray();
                var tasks = new Task[ files.Length ];
                for ( var i = 0; i < files.Length; i++ )
                {
                    var file = files[ i ];
                    var task = Task.Run( async () =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        
                        try
                        {
                            await semaphore.WaitAsync().ConfigureAwait( false );
                            cts.Token.ThrowIfCancellationRequested();

                            string fileFinalPath = Path.Combine( depot.installDir, file.FileName );
                            string fileStagingPath = Path.Combine( stagingDir, file.FileName );

                            // This may still exist if the previous run exited before cleanup
                            if ( File.Exists( fileStagingPath ) )
                            {
                                File.Delete( fileStagingPath );
                            }

                            FileStream fs = null;
                            List<ProtoManifest.ChunkData> neededChunks;
                            FileInfo fi = new FileInfo( fileFinalPath );
                            if ( !fi.Exists )
                            {
                                // create new file. need all chunks
                                fs = File.Create( fileFinalPath );
                                fs.SetLength( ( long )file.TotalSize );
                                neededChunks = new List<ProtoManifest.ChunkData>( file.Chunks );
                            }
                            else
                            {
                                // open existing
                                ProtoManifest.FileData oldManifestFile = null;
                                if ( oldProtoManifest != null )
                                {
                                    oldManifestFile = oldProtoManifest.Files.SingleOrDefault( f => f.FileName == file.FileName );
                                }

                                if ( oldManifestFile != null )
                                {
                                    neededChunks = new List<ProtoManifest.ChunkData>();

                                    if ( Config.VerifyAll || !oldManifestFile.FileHash.SequenceEqual( file.FileHash ) )
                                    {
                                        // we have a version of this file, but it doesn't fully match what we want

                                        var matchingChunks = new List<ChunkMatch>();

                                        foreach ( var chunk in file.Chunks )
                                        {
                                            var oldChunk = oldManifestFile.Chunks.FirstOrDefault( c => c.ChunkID.SequenceEqual( chunk.ChunkID ) );
                                            if ( oldChunk != null )
                                            {
                                                matchingChunks.Add( new ChunkMatch( oldChunk, chunk ) );
                                            }
                                            else
                                            {
                                                neededChunks.Add( chunk );
                                            }
                                        }

                                        File.Move( fileFinalPath, fileStagingPath );

                                        fs = File.Open( fileFinalPath, FileMode.Create );
                                        fs.SetLength( ( long )file.TotalSize );

                                        using ( var fsOld = File.Open( fileStagingPath, FileMode.Open ) )
                                        {
                                            foreach ( var match in matchingChunks )
                                            {
                                                fsOld.Seek( ( long )match.OldChunk.Offset, SeekOrigin.Begin );

                                                byte[] tmp = new byte[ match.OldChunk.UncompressedLength ];
                                                fsOld.Read( tmp, 0, tmp.Length );

                                                byte[] adler = Util.AdlerHash( tmp );
                                                if ( !adler.SequenceEqual( match.OldChunk.Checksum ) )
                                                {
                                                    neededChunks.Add( match.NewChunk );
                                                }
                                                else
                                                {
                                                    fs.Seek( ( long )match.NewChunk.Offset, SeekOrigin.Begin );
                                                    fs.Write( tmp, 0, tmp.Length );
                                                }
                                            }
                                        }

                                        File.Delete( fileStagingPath );
                                    }
                                }
                                else
                                {
                                    // No old manifest or file not in old manifest. We must validate.

                                    fs = File.Open( fileFinalPath, FileMode.Open );
                                    if ( ( ulong )fi.Length != file.TotalSize )
                                    {
                                        fs.SetLength( ( long )file.TotalSize );
                                    }

                                    neededChunks = Util.ValidateSteam3FileChecksums( fs, file.Chunks.OrderBy( x => x.Offset ).ToArray() );
                                }

                                if ( neededChunks.Count() == 0 )
                                {
                                    size_downloaded += file.TotalSize;
                                    WriteToLog(
                                        $"{((float) size_downloaded / (float) complete_download_size) * 100.0f,6:#00.00}% {fileFinalPath}");
                                    if ( fs != null )
                                        fs.Dispose();
                                    return;
                                }
                                else
                                {
                                    size_downloaded += ( file.TotalSize - ( ulong )neededChunks.Select( x => ( long )x.UncompressedLength ).Sum() );
                                }
                            }

                            foreach ( var chunk in neededChunks )
                            {
                                if ( cts.IsCancellationRequested ) break;

                                string chunkID = Util.EncodeHexString( chunk.ChunkID );
                                CDNClient.DepotChunk chunkData = null;

                                while ( !cts.IsCancellationRequested )
                                {
                                    CDNClient client;
                                    try
                                    {
                                        client = await cdnPool.GetConnectionForDepotAsync( appId, depot.id, depot.depotKey, cts.Token ).ConfigureAwait( false );
                                    }
                                    catch ( OperationCanceledException )
                                    {
                                        break;
                                    }

                                    DepotManifest.ChunkData data = new DepotManifest.ChunkData();
                                    data.ChunkID = chunk.ChunkID;
                                    data.Checksum = chunk.Checksum;
                                    data.Offset = chunk.Offset;
                                    data.CompressedLength = chunk.CompressedLength;
                                    data.UncompressedLength = chunk.UncompressedLength;

                                    try
                                    {
                                        chunkData = await client.DownloadDepotChunkAsync( depot.id, data ).ConfigureAwait( false );
                                        cdnPool.ReturnConnection( client );
                                        break;
                                    }
                                    catch ( WebException e )
                                    {
                                        cdnPool.ReturnBrokenConnection( client );

                                        if ( e.Status == WebExceptionStatus.ProtocolError )
                                        {
                                            var response = e.Response as HttpWebResponse;
                                            if ( response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden )
                                            {
                                                WriteToLog($"Encountered 401 for chunk {chunkID}. Aborting.");
                                                cts.Cancel();
                                                break;
                                            }
                                            else
                                            {
                                                WriteToLog(
                                                    $"Encountered error downloading chunk {chunkID}: {response.StatusCode}");
                                            }
                                        }
                                        else
                                        {
                                            WriteToLog($"Encountered error downloading chunk {chunkID}: {e.Status}");
                                        }
                                    }
                                    catch ( Exception e )
                                    {
                                        cdnPool.ReturnBrokenConnection( client );
                                        WriteToLog(
                                            $"Encountered unexpected error downloading chunk {chunkID}: {e.Message}");
                                    }
                                }

                                if ( chunkData == null )
                                {
                                    WriteToLog(
                                        $"Failed to find any server with chunk {chunkID} for depot {depot.id}. Aborting.");
                                    cts.Cancel();
                                    return;
                                }

                                TotalBytesCompressed += chunk.CompressedLength;
                                DepotBytesCompressed += chunk.CompressedLength;
                                TotalBytesUncompressed += chunk.UncompressedLength;
                                DepotBytesUncompressed += chunk.UncompressedLength;

                                fs.Seek( ( long )chunk.Offset, SeekOrigin.Begin );
                                fs.Write( chunkData.Data, 0, chunkData.Data.Length );

                                size_downloaded += chunk.UncompressedLength;
                            }

                            fs.Dispose();

                            WriteToLog(
                                $"{((float) size_downloaded / (float) complete_download_size) * 100.0f,6:#00.00}% {fileFinalPath}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    } );

                    tasks[ i ] = task;
                }

                await Task.WhenAll( tasks ).ConfigureAwait( false );

                ConfigStore.TheConfig.LastManifests[ depot.id ] = depot.manifestId;
                ConfigStore.Save();

                WriteToLog(
                    $"Depot {depot.id} - Downloaded {DepotBytesCompressed} bytes ({DepotBytesUncompressed} bytes uncompressed)");
            }

            WriteToLog(
                $"Total downloaded: {TotalBytesCompressed} bytes ({TotalBytesUncompressed} bytes uncompressed) from {depots.Count} depots");
        }
    }
}
