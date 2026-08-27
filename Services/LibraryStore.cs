using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using GlamSpector.Models;
using Microsoft.Data.Sqlite;

namespace GlamSpector.Services;

public sealed class LibraryStore
{
    private readonly string connectionString;

    private static readonly Regex CaptureFileName = new(
        @"^(?<character>.+)_(?<world>[^_]+)_(?<date>\d{4}-\d{2}-\d{2})_(?<time>\d{2}-\d{2}-\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string DatabasePath { get; }
    public string MediaRoot { get; }

    public LibraryStore(string databasePath, string? mediaRoot = null)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        MediaRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(mediaRoot)
            ? Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "LibraryMedia")
            : mediaRoot);
        Directory.CreateDirectory(MediaRoot);

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        Initialize();
    }

    public string CreateCaptureMediaDirectory(string suggestedName)
    {
        var root = Path.Combine(MediaRoot, "Captures");
        Directory.CreateDirectory(root);
        return CreateUniqueDirectory(root, MakeSafeFilePart(suggestedName));
    }

    public string CreateImportedMediaDirectory(string suggestedName)
    {
        var root = Path.Combine(MediaRoot, "Imported");
        Directory.CreateDirectory(root);
        return CreateUniqueDirectory(root, MakeSafeFilePart(suggestedName));
    }

    private static string CreateUniqueDirectory(string root, string suggestedName)
    {
        var name = string.IsNullOrWhiteSpace(suggestedName) ? "Entry" : suggestedName;
        for (var i = 1; i < 10000; i++)
        {
            var suffix = i == 1 ? string.Empty : $"_{i}";
            var candidate = Path.Combine(root, name + suffix);
            if (Directory.Exists(candidate))
                continue;
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        throw new IOException("Could not create a unique GlamSpector media directory.");
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS library_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at_utc TEXT NOT NULL,
                character_name TEXT NOT NULL,
                home_world TEXT NOT NULL,
                free_company_name TEXT NULL,
                card_path TEXT NOT NULL UNIQUE,
                raw_preview_path TEXT NULL,
                diagnostic_json_path TEXT NULL,
                facewear_id INTEGER NOT NULL DEFAULT 0,
                facewear_name TEXT NULL,
                adventurer_plate_path TEXT NULL,
                portrait_settings_json TEXT NULL,
                rating INTEGER NOT NULL DEFAULT 0,
                notes TEXT NULL,
                source_kind TEXT NULL,
                source_url TEXT NULL,
                source_title TEXT NULL,
                source_creator TEXT NULL,
                display_title TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS library_pieces (
                entry_id INTEGER NOT NULL,
                raw_slot_index INTEGER NOT NULL,
                slot_name TEXT NOT NULL,
                equipped_item_id INTEGER NOT NULL,
                glamour_item_id INTEGER NOT NULL,
                display_item_id INTEGER NOT NULL,
                display_item_name TEXT NOT NULL,
                stain1_id INTEGER NOT NULL,
                stain1_name TEXT NULL,
                stain2_id INTEGER NOT NULL,
                stain2_name TEXT NULL,
                PRIMARY KEY (entry_id, raw_slot_index),
                FOREIGN KEY (entry_id) REFERENCES library_entries(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS wanted_items (
                item_id INTEGER PRIMARY KEY,
                item_name TEXT NOT NULL,
                added_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS library_tags (
                entry_id INTEGER NOT NULL,
                tag TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (entry_id, tag),
                FOREIGN KEY (entry_id) REFERENCES library_entries(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS library_source_images (
                entry_id INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                path TEXT NOT NULL,
                PRIMARY KEY (entry_id, ordinal),
                FOREIGN KEY (entry_id) REFERENCES library_entries(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS library_personal_previews (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                is_primary INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (entry_id) REFERENCES library_entries(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS library_generated_share_cards (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id INTEGER NOT NULL,
                personal_preview_id INTEGER NULL,
                created_at_utc TEXT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                FOREIGN KEY (entry_id) REFERENCES library_entries(id) ON DELETE CASCADE,
                FOREIGN KEY (personal_preview_id) REFERENCES library_personal_previews(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS idx_library_entries_captured_at
                ON library_entries(captured_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_library_pieces_item_name
                ON library_pieces(display_item_name);
            CREATE INDEX IF NOT EXISTS idx_wanted_items_name
                ON wanted_items(item_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_library_tags_tag
                ON library_tags(tag COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_library_source_images_entry
                ON library_source_images(entry_id, ordinal);
            CREATE INDEX IF NOT EXISTS idx_library_personal_previews_entry
                ON library_personal_previews(entry_id, created_at_utc);
            CREATE INDEX IF NOT EXISTS idx_library_generated_share_cards_entry
                ON library_generated_share_cards(entry_id, created_at_utc);
            """;
        command.ExecuteNonQuery();

        // M3.12 initially only made a personal preview Primary when an entry had
        // no usable card/source image. M3.14 is preview-first, so older entries
        // with personal previews but no explicit Primary get their newest preview
        // promoted once. Existing user-selected Primary previews are preserved.
        using (var promoteLegacyPreview = connection.CreateCommand())
        {
            promoteLegacyPreview.CommandText = """
                UPDATE library_personal_previews
                SET is_primary = 1
                WHERE id IN (
                    SELECT p.id
                    FROM library_personal_previews p
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM library_personal_previews existing
                        WHERE existing.entry_id = p.entry_id
                          AND existing.is_primary = 1
                    )
                      AND p.id = (
                          SELECT newest.id
                          FROM library_personal_previews newest
                          WHERE newest.entry_id = p.entry_id
                          ORDER BY newest.created_at_utc DESC, newest.id DESC
                          LIMIT 1
                      )
                );
                """;
            promoteLegacyPreview.ExecuteNonQuery();
        }

        // Existing M3.0/M3.1 libraries predate Free Company metadata.
        EnsureColumn(connection, "library_entries", "free_company_name", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "adventurer_plate_path", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "portrait_settings_json", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "rating", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "library_entries", "notes", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "source_kind", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "source_url", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "source_title", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "source_creator", "TEXT NULL");
        EnsureColumn(connection, "library_entries", "display_title", "TEXT NULL");

        // M3.15 separates the user's local display title from capture/import
        // identity. Preserve the exact old Character @ World label for existing
        // non-EC entries. EC already has a dedicated source title/creator/URL, so
        // its local title starts from that source title without the old synthetic
        // "@ Eorzea Collection" suffix. No media or recipe data is rewritten.
        using var backfillDisplayTitles = connection.CreateCommand();
        backfillDisplayTitles.CommandText = """
            UPDATE library_entries
            SET display_title = CASE
                WHEN source_kind = 'EorzeaCollection' COLLATE NOCASE THEN
                    COALESCE(
                        NULLIF(TRIM(source_title), ''),
                        NULLIF(TRIM(character_name), ''),
                        'Untitled glamour')
                ELSE TRIM(character_name) || ' @ ' || TRIM(home_world)
            END
            WHERE display_title IS NULL OR TRIM(display_title) = '';
            """;
        backfillDisplayTitles.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    public long AddCapture(
        GlamourSnapshot snapshot,
        string cardPath,
        string? rawPreviewPath,
        string? diagnosticJsonPath,
        Func<bool>? canCommit = null)
    {
        if (canCommit is not null && !canCommit())
            throw new OperationCanceledException("The capture no longer owns Library publication.");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO library_entries (
                    captured_at_utc,
                    character_name,
                    home_world,
                    free_company_name,
                    card_path,
                    raw_preview_path,
                    diagnostic_json_path,
                    facewear_id,
                    facewear_name,
                    adventurer_plate_path,
                    display_title
                ) VALUES (
                    $captured_at_utc,
                    $character_name,
                    $home_world,
                    $free_company_name,
                    $card_path,
                    $raw_preview_path,
                    $diagnostic_json_path,
                    $facewear_id,
                    $facewear_name,
                    $adventurer_plate_path,
                    $display_title
                )
                ON CONFLICT(card_path) DO UPDATE SET
                    captured_at_utc = excluded.captured_at_utc,
                    character_name = excluded.character_name,
                    home_world = excluded.home_world,
                    free_company_name = excluded.free_company_name,
                    raw_preview_path = excluded.raw_preview_path,
                    diagnostic_json_path = excluded.diagnostic_json_path,
                    facewear_id = excluded.facewear_id,
                    facewear_name = excluded.facewear_name;
                """;

            insert.Parameters.AddWithValue("$captured_at_utc", snapshot.CapturedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$character_name", snapshot.CharacterName);
            insert.Parameters.AddWithValue("$home_world", snapshot.HomeWorld);
            insert.Parameters.AddWithValue("$free_company_name", DbValue(snapshot.FreeCompanyName));
            insert.Parameters.AddWithValue("$card_path", Path.GetFullPath(cardPath));
            insert.Parameters.AddWithValue("$raw_preview_path", DbValue(rawPreviewPath));
            insert.Parameters.AddWithValue("$diagnostic_json_path", DbValue(diagnosticJsonPath));
            insert.Parameters.AddWithValue("$facewear_id", snapshot.Facewear?.GlassesId0 ?? 0);
            insert.Parameters.AddWithValue("$facewear_name", DbValue(snapshot.Facewear?.DisplayName));
            insert.Parameters.AddWithValue("$adventurer_plate_path", DBNull.Value);
            insert.Parameters.AddWithValue("$display_title", CreateDefaultDisplayTitle(snapshot.CharacterName, snapshot.HomeWorld));
            insert.ExecuteNonQuery();
        }

        long entryId;
        using (var selectId = connection.CreateCommand())
        {
            selectId.Transaction = transaction;
            selectId.CommandText = "SELECT id FROM library_entries WHERE card_path = $card_path;";
            selectId.Parameters.AddWithValue("$card_path", Path.GetFullPath(cardPath));
            entryId = Convert.ToInt64(selectId.ExecuteScalar());
        }

        using (var deletePieces = connection.CreateCommand())
        {
            deletePieces.Transaction = transaction;
            deletePieces.CommandText = "DELETE FROM library_pieces WHERE entry_id = $entry_id;";
            deletePieces.Parameters.AddWithValue("$entry_id", entryId);
            deletePieces.ExecuteNonQuery();
        }

        foreach (var piece in snapshot.Pieces)
        {
            using var insertPiece = connection.CreateCommand();
            insertPiece.Transaction = transaction;
            insertPiece.CommandText = """
                INSERT INTO library_pieces (
                    entry_id,
                    raw_slot_index,
                    slot_name,
                    equipped_item_id,
                    glamour_item_id,
                    display_item_id,
                    display_item_name,
                    stain1_id,
                    stain1_name,
                    stain2_id,
                    stain2_name
                ) VALUES (
                    $entry_id,
                    $raw_slot_index,
                    $slot_name,
                    $equipped_item_id,
                    $glamour_item_id,
                    $display_item_id,
                    $display_item_name,
                    $stain1_id,
                    $stain1_name,
                    $stain2_id,
                    $stain2_name
                );
                """;

            insertPiece.Parameters.AddWithValue("$entry_id", entryId);
            insertPiece.Parameters.AddWithValue("$raw_slot_index", piece.RawSlotIndex);
            insertPiece.Parameters.AddWithValue("$slot_name", piece.SlotName);
            insertPiece.Parameters.AddWithValue("$equipped_item_id", piece.EquippedItemId);
            insertPiece.Parameters.AddWithValue("$glamour_item_id", piece.GlamourItemId);
            insertPiece.Parameters.AddWithValue("$display_item_id", piece.DisplayItemId);
            insertPiece.Parameters.AddWithValue("$display_item_name", piece.DisplayItemName);
            insertPiece.Parameters.AddWithValue("$stain1_id", piece.Stain1Id);
            insertPiece.Parameters.AddWithValue("$stain1_name", DbValue(piece.Stain1Name));
            insertPiece.Parameters.AddWithValue("$stain2_id", piece.Stain2Id);
            insertPiece.Parameters.AddWithValue("$stain2_name", DbValue(piece.Stain2Name));
            insertPiece.ExecuteNonQuery();
        }

        if (canCommit is not null && !canCommit())
            throw new OperationCanceledException("The capture was retired before Library commit.");

        transaction.Commit();
        return entryId;
    }

    public long AddGlamCode(GlamourSnapshot snapshot, string glamCode)
    {
        var normalized = GlamCodeService.Normalize(glamCode);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..20];

        // Preserve the exact legacy path when an older M3.10/M3.11 installation
        // already indexed this code, otherwise new code-only entries get their
        // own managed media folder. This keeps re-import identity stable across
        // the M3.12 storage layout change.
        var legacyDirectory = Path.Combine(Path.GetDirectoryName(DatabasePath) ?? ".", "GlamCodes");
        var legacyPath = Path.Combine(legacyDirectory, hash + ".glamcode");
        string codePath;
        if (ContainsCardPath(legacyPath) || File.Exists(legacyPath))
        {
            Directory.CreateDirectory(legacyDirectory);
            codePath = legacyPath;
        }
        else
        {
            var codeDirectory = Path.Combine(MediaRoot, "GlamCodes", hash);
            Directory.CreateDirectory(codeDirectory);
            codePath = Path.Combine(codeDirectory, "recipe.glamcode");
        }

        File.WriteAllText(codePath, normalized);

        // Re-importing the exact same code resolves to the same card_path, so the
        // existing entry is updated rather than duplicated.
        return AddCapture(snapshot, codePath, null, null);
    }

    public long AddEorzeaCollectionImport(EorzeaCollectionImportResult imported)
    {
        var normalizedCardPath = Path.GetFullPath(imported.CardPath);
        // A row can already exist at the resolved card path even when an older
        // or interrupted EC import never finished writing its source metadata.
        // Treat either identity route as an existing entry so a local rename is
        // never mistaken for a title that still needs initialization.
        var entryExistedBeforeImport = ContainsCardPath(normalizedCardPath);

        // Treat the EC glamour ID as the stable identity, not the first image
        // filename. An import may initially have no downloadable picture (using
        // an .ecglam marker) and gain source-01.png on a later refresh. Re-point
        // the existing row before AddCapture so ratings/tags/notes remain attached
        // instead of creating a duplicate Library entry.
        using (var lookupConnection = OpenConnection())
        using (var lookup = lookupConnection.CreateCommand())
        {
            lookup.CommandText = """
                SELECT id, card_path
                FROM library_entries
                WHERE source_kind = 'EorzeaCollection'
                  AND (
                      source_url = $source_url
                      OR source_url LIKE $id_with_slug
                      OR source_url LIKE $id_without_slug
                  )
                ORDER BY id DESC
                LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("$source_url", imported.SourceUrl);
            lookup.Parameters.AddWithValue("$id_with_slug", $"%/glamour/{imported.GlamourId}/%");
            lookup.Parameters.AddWithValue("$id_without_slug", $"%/glamour/{imported.GlamourId}");

            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                entryExistedBeforeImport = true;
                var existingId = reader.GetInt64(0);
                var existingCardPath = reader.GetString(1);
                reader.Close();
                // Manual-only EC imports never download replacement media. If
                // an older entry already points at a valid cached source image,
                // keep that image as its card rather than repointing it to a new
                // image-less marker during metadata refresh.
                if (imported.SourceImagePaths.Count == 0 &&
                    !IsEorzeaCollectionMarkerPath(existingCardPath) &&
                    File.Exists(existingCardPath))
                {
                    normalizedCardPath = Path.GetFullPath(existingCardPath);
                }
                if (!string.Equals(Path.GetFullPath(existingCardPath), normalizedCardPath, StringComparison.OrdinalIgnoreCase))
                {
                    using var repoint = lookupConnection.CreateCommand();
                    repoint.CommandText = "UPDATE library_entries SET card_path = $card_path WHERE id = $id;";
                    repoint.Parameters.AddWithValue("$card_path", normalizedCardPath);
                    repoint.Parameters.AddWithValue("$id", existingId);
                    repoint.ExecuteNonQuery();
                }
            }
        }

        var entryId = AddCapture(imported.Snapshot, normalizedCardPath, null, null);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE library_entries
                SET source_kind = $source_kind,
                    source_url = $source_url,
                    source_title = $source_title,
                    source_creator = $source_creator,
                    display_title = CASE
                        WHEN $set_initial_display_title = 1 THEN $display_title
                        ELSE display_title
                    END
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$source_kind", "EorzeaCollection");
            update.Parameters.AddWithValue("$source_url", imported.SourceUrl);
            update.Parameters.AddWithValue("$source_title", imported.Title);
            update.Parameters.AddWithValue("$source_creator", DbValue(imported.Creator));
            update.Parameters.AddWithValue("$set_initial_display_title", entryExistedBeforeImport ? 0 : 1);
            update.Parameters.AddWithValue("$display_title", CreateInitialDisplayTitle(imported.Title, "Eorzea Collection glamour"));
            update.Parameters.AddWithValue("$id", entryId);
            update.ExecuteNonQuery();
        }

        // An empty list now means no new media was supplied, not that the user
        // asked to remove older cached EC images. Preserve legacy rows/paths.
        if (imported.SourceImagePaths.Count > 0)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM library_source_images WHERE entry_id = $entry_id;";
                delete.Parameters.AddWithValue("$entry_id", entryId);
                delete.ExecuteNonQuery();
            }

            for (var i = 0; i < imported.SourceImagePaths.Count; i++)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO library_source_images (entry_id, ordinal, path) VALUES ($entry_id, $ordinal, $path);";
                insert.Parameters.AddWithValue("$entry_id", entryId);
                insert.Parameters.AddWithValue("$ordinal", i);
                insert.Parameters.AddWithValue("$path", Path.GetFullPath(imported.SourceImagePaths[i]));
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return entryId;
    }

    public static bool IsGlamCodePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".glamcode", StringComparison.OrdinalIgnoreCase);

    public static bool IsEorzeaCollectionMarkerPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".ecglam", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageLessPath(string? path) =>
        IsGlamCodePath(path) || IsEorzeaCollectionMarkerPath(path);

    public LibraryImportResult ImportExistingCaptures(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("The configured GlamSpector output folder does not exist.");

        var fullMetadataImported = 0;
        var imageOnlyImported = 0;
        var existingSkipped = 0;
        var failed = 0;

        // Older GlamSpector versions used a flat output directory. M3.12 keeps
        // newly indexed captures in LibraryMedia/Captures/<entry>/glam-card.png.
        // Scan both layouts, but deliberately do not recurse through arbitrary
        // folders where source/personal images could be mistaken for Glam Cards.
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly))
            candidates.Add(Path.GetFullPath(path));

        var managedCaptureRoot = Path.Combine(MediaRoot, "Captures");
        if (Directory.Exists(managedCaptureRoot))
        {
            foreach (var path in Directory.EnumerateFiles(managedCaptureRoot, "glam-card.png", SearchOption.AllDirectories))
                candidates.Add(Path.GetFullPath(path));
        }

        foreach (var cardPath in candidates)
        {
            var fileName = Path.GetFileName(cardPath);
            if (fileName.EndsWith("_preview.png", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("_plate.png", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("raw-preview.png", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("adventurer-plate.png", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("my-preview-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("source-", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var fullCardPath = Path.GetFullPath(cardPath);
                if (ContainsCardPath(fullCardPath))
                {
                    existingSkipped++;
                    continue;
                }

                var managedLayout = fileName.Equals("glam-card.png", StringComparison.OrdinalIgnoreCase) &&
                                    IsPathUnderRoot(fullCardPath, managedCaptureRoot);

                string jsonPath;
                string rawPreviewPath;
                string platePath;
                if (managedLayout)
                {
                    var directory = Path.GetDirectoryName(fullCardPath) ?? managedCaptureRoot;
                    jsonPath = Path.Combine(directory, "diagnostic.json");
                    rawPreviewPath = Path.Combine(directory, "raw-preview.png");
                    platePath = Path.Combine(directory, "adventurer-plate.png");
                    if (!File.Exists(platePath))
                        platePath = Path.Combine(directory, "glam-card_plate.png"); // early M3.12/dev compatibility
                }
                else
                {
                    var basePath = Path.Combine(
                        Path.GetDirectoryName(fullCardPath) ?? folder,
                        Path.GetFileNameWithoutExtension(fullCardPath));
                    jsonPath = basePath + ".json";
                    rawPreviewPath = basePath + "_preview.png";
                    platePath = basePath + "_plate.png";
                }

                GlamourSnapshot snapshot;
                var hasFullMetadata = false;

                if (File.Exists(jsonPath))
                {
                    try
                    {
                        snapshot = ReadSnapshotFromJson(jsonPath, fullCardPath);
                        hasFullMetadata = snapshot.Pieces.Count > 0 || snapshot.Facewear?.Detected == true;
                    }
                    catch
                    {
                        snapshot = managedLayout
                            ? CreateManagedImageOnlySnapshot(fullCardPath)
                            : CreateImageOnlySnapshot(fullCardPath);
                    }
                }
                else
                {
                    snapshot = managedLayout
                        ? CreateManagedImageOnlySnapshot(fullCardPath)
                        : CreateImageOnlySnapshot(fullCardPath);
                }

                var entryId = AddCapture(
                    snapshot,
                    fullCardPath,
                    File.Exists(rawPreviewPath) ? rawPreviewPath : null,
                    File.Exists(jsonPath) ? jsonPath : null);
                if (File.Exists(platePath))
                    SetAdventurerPlatePath(entryId, platePath);

                if (hasFullMetadata)
                    fullMetadataImported++;
                else
                    imageOnlyImported++;
            }
            catch
            {
                failed++;
            }
        }

        return new LibraryImportResult
        {
            FullMetadataImported = fullMetadataImported,
            ImageOnlyImported = imageOnlyImported,
            ExistingSkipped = existingSkipped,
            Failed = failed,
        };
    }

    private static GlamourSnapshot CreateManagedImageOnlySnapshot(string cardPath)
    {
        var directoryName = Path.GetFileName(Path.GetDirectoryName(cardPath) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            // Managed capture folders are named after the old flat capture stem.
            // Temporarily synthesize that stem for the same parser, while taking
            // the timestamp from the real card file below when necessary.
            var match = CaptureFileName.Match(directoryName);
            if (match.Success)
            {
                var characterName = match.Groups["character"].Value;
                var homeWorld = match.Groups["world"].Value;
                var localStamp = $"{match.Groups["date"].Value}_{match.Groups["time"].Value}";
                var capturedAtUtc = DateTime.TryParseExact(
                    localStamp,
                    "yyyy-MM-dd_HH-mm-ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var localTime)
                    ? localTime.ToUniversalTime()
                    : File.GetLastWriteTimeUtc(cardPath);

                return new GlamourSnapshot
                {
                    CapturedAtUtc = capturedAtUtc,
                    CharacterName = characterName,
                    HomeWorld = homeWorld,
                    Pieces = [],
                };
            }
        }

        return new GlamourSnapshot
        {
            CapturedAtUtc = File.GetLastWriteTimeUtc(cardPath),
            CharacterName = "Unknown Character",
            HomeWorld = "Unknown World",
            Pieces = [],
        };
    }

    public int CountEntries()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM library_entries;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public List<LibraryEntry> Search(string? query, LibrarySort sort = LibrarySort.Newest, int limit = 250)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        var trimmed = query?.Trim() ?? string.Empty;
        var orderBy = sort switch
        {
            LibrarySort.Oldest => "e.captured_at_utc ASC",
            LibrarySort.Character => "e.character_name COLLATE NOCASE ASC, e.captured_at_utc DESC",
            LibrarySort.World => "e.home_world COLLATE NOCASE ASC, e.character_name COLLATE NOCASE ASC, e.captured_at_utc DESC",
            LibrarySort.Rating => "e.rating DESC, e.captured_at_utc DESC",
            _ => "e.captured_at_utc DESC",
        };

        command.CommandText = $"""
            SELECT
                e.id,
                e.captured_at_utc,
                e.character_name,
                e.home_world,
                e.card_path,
                e.raw_preview_path,
                e.diagnostic_json_path,
                e.facewear_id,
                e.facewear_name,
                e.free_company_name,
                e.adventurer_plate_path,
                e.portrait_settings_json,
                e.rating,
                e.notes,
                e.source_kind,
                e.source_url,
                e.source_title,
                e.source_creator,
                e.display_title
            FROM library_entries e
            WHERE
                $query = ''
                OR e.character_name LIKE $like COLLATE NOCASE
                OR e.home_world LIKE $like COLLATE NOCASE
                OR e.free_company_name LIKE $like COLLATE NOCASE
                OR e.facewear_name LIKE $like COLLATE NOCASE
                OR e.notes LIKE $like COLLATE NOCASE
                OR e.display_title LIKE $like COLLATE NOCASE
                OR e.source_title LIKE $like COLLATE NOCASE
                OR e.source_creator LIKE $like COLLATE NOCASE
                OR e.source_url LIKE $like COLLATE NOCASE
                OR EXISTS (
                    SELECT 1
                    FROM library_tags t
                    WHERE t.entry_id = e.id
                      AND t.tag LIKE $like COLLATE NOCASE
                )
                OR EXISTS (
                    SELECT 1
                    FROM library_pieces p
                    WHERE p.entry_id = e.id
                      AND (
                          p.display_item_name LIKE $like COLLATE NOCASE
                          OR p.slot_name LIKE $like COLLATE NOCASE
                          OR p.stain1_name LIKE $like COLLATE NOCASE
                          OR p.stain2_name LIKE $like COLLATE NOCASE
                      )
                )
            ORDER BY {orderBy}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", trimmed);
        command.Parameters.AddWithValue("$like", $"%{trimmed}%");
        var queryLimit = sort == LibrarySort.FileSize ? Math.Max(Math.Max(1, limit), 10000) : Math.Max(1, limit);
        command.Parameters.AddWithValue("$limit", queryLimit);

        var results = new List<LibraryEntry>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                results.Add(ReadEntry(reader));
        }

        PopulatePieces(connection, results);
        PopulateTags(connection, results);
        PopulateSourceImages(connection, results);
        PopulatePersonalPreviews(connection, results);
        PopulateGeneratedShareCards(connection, results);
        PopulateMediaSizes(results);

        if (sort == LibrarySort.FileSize)
        {
            results = results
                .OrderByDescending(entry => entry.TotalMediaBytes)
                .ThenByDescending(entry => entry.CapturedAtUtc)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        return results;
    }


    public List<LibraryEntry> FindOlderDuplicates()
    {
        // Image-only imports cannot be compared safely because they have no
        // structured gear metadata. For normal captures, duplicate identity is
        // intentionally strict: same character/world, same Facewear and the
        // exact same equipped/glamour/display IDs + dyes in every recorded slot.
        var summaries = Search(string.Empty, LibrarySort.Newest, 10000);
        var fullEntries = new List<LibraryEntry>();
        foreach (var summary in summaries)
        {
            var full = Get(summary.Id);
            if (full is not null && full.Pieces.Count > 0)
                fullEntries.Add(full);
        }

        return fullEntries
            .GroupBy(BuildDuplicateKey, StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(entry => entry.CapturedAtUtc)
                .ThenByDescending(entry => entry.Id)
                .Skip(1))
            .OrderByDescending(entry => entry.CapturedAtUtc)
            .ToList();
    }

    public int CleanupOlderDuplicates(bool deleteFiles)
    {
        var duplicates = FindOlderDuplicates();
        foreach (var entry in duplicates)
        {
            if (deleteFiles)
                DeleteWithFiles(entry);
            else
                Delete(entry.Id);
        }

        return duplicates.Count;
    }

    private static string BuildDuplicateKey(LibraryEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.CharacterName.Trim().ToUpperInvariant())
            .Append('|')
            .Append(entry.HomeWorld.Trim().ToUpperInvariant())
            .Append('|')
            .Append(entry.FacewearId);

        foreach (var piece in entry.Pieces.OrderBy(piece => piece.RawSlotIndex))
        {
            // Duplicate detection follows what GlamSpector catalogues: the
            // visible glamour item and dyes. The hidden equipped/stat item is not
            // part of the look and must not split otherwise identical captures.
            builder.Append('|')
                .Append(piece.RawSlotIndex).Append(':')
                .Append(piece.DisplayItemId).Append(':')
                .Append(piece.Stain1Id).Append(':')
                .Append(piece.Stain2Id);
        }

        return builder.ToString();
    }

    public LibraryEntry? Get(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                e.id,
                e.captured_at_utc,
                e.character_name,
                e.home_world,
                e.card_path,
                e.raw_preview_path,
                e.diagnostic_json_path,
                e.facewear_id,
                e.facewear_name,
                e.free_company_name,
                e.adventurer_plate_path,
                e.portrait_settings_json,
                e.rating,
                e.notes,
                e.source_kind,
                e.source_url,
                e.source_title,
                e.source_creator,
                e.display_title
            FROM library_entries e
            WHERE e.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        LibraryEntry? entry;
        using (var reader = command.ExecuteReader())
        {
            entry = reader.Read() ? ReadEntry(reader) : null;
        }

        if (entry is null)
            return null;

        using var piecesCommand = connection.CreateCommand();
        piecesCommand.CommandText = """
            SELECT
                raw_slot_index,
                slot_name,
                equipped_item_id,
                glamour_item_id,
                display_item_id,
                display_item_name,
                stain1_id,
                stain1_name,
                stain2_id,
                stain2_name
            FROM library_pieces
            WHERE entry_id = $entry_id
            ORDER BY raw_slot_index;
            """;
        piecesCommand.Parameters.AddWithValue("$entry_id", id);

        using var piecesReader = piecesCommand.ExecuteReader();
        while (piecesReader.Read())
        {
            entry.Pieces.Add(new GlamourPiece
            {
                RawSlotIndex = piecesReader.GetInt32(0),
                SlotName = piecesReader.GetString(1),
                EquippedItemId = checked((uint)piecesReader.GetInt64(2)),
                GlamourItemId = checked((uint)piecesReader.GetInt64(3)),
                DisplayItemId = checked((uint)piecesReader.GetInt64(4)),
                DisplayItemName = piecesReader.GetString(5),
                Stain1Id = checked((byte)piecesReader.GetInt32(6)),
                Stain1Name = GetNullableString(piecesReader, 7),
                Stain2Id = checked((byte)piecesReader.GetInt32(8)),
                Stain2Name = GetNullableString(piecesReader, 9),
            });
        }
        piecesReader.Close();

        PopulateTags(connection, new List<LibraryEntry> { entry });
        PopulateSourceImages(connection, new List<LibraryEntry> { entry });
        PopulatePersonalPreviews(connection, new List<LibraryEntry> { entry });
        PopulateGeneratedShareCards(connection, new List<LibraryEntry> { entry });
        PopulateMediaSizes(new List<LibraryEntry> { entry });
        return entry;
    }

    public void SetAdventurerPlatePath(long id, string? path)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_entries SET adventurer_plate_path = $path WHERE id = $id;";
        command.Parameters.AddWithValue("$path", DbValue(path));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetPortraitSettings(long id, PortraitSettingsSnapshot? settings)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_entries SET portrait_settings_json = $json WHERE id = $id;";
        command.Parameters.AddWithValue("$json", settings is null
            ? DBNull.Value
            : JsonSerializer.Serialize(settings));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public string AddPersonalPreview(long entryId, ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.IsEmpty)
            throw new InvalidDataException("The captured personal preview image is empty.");

        var entry = Get(entryId) ?? throw new InvalidOperationException("The selected Library entry no longer exists.");
        var entryDirectory = GetOrCreateEntryMediaDirectory(entry);
        var previewsDirectory = Path.Combine(entryDirectory, "previews");
        Directory.CreateDirectory(previewsDirectory);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        var path = GetUniquePath(Path.Combine(previewsDirectory, $"my-preview-{stamp}.png"));
        File.WriteAllBytes(path, pngBytes.ToArray());

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            // M3.14 is preview-first: every fresh personal preview becomes the
            // Library primary immediately. Older previews and original cards stay
            // available as secondary media and can be selected again explicitly.
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "UPDATE library_personal_previews SET is_primary = 0 WHERE entry_id = $entry_id;";
                clear.Parameters.AddWithValue("$entry_id", entryId);
                clear.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO library_personal_previews (entry_id, created_at_utc, path, is_primary)
                VALUES ($entry_id, $created_at_utc, $path, $is_primary);
                """;
            insert.Parameters.AddWithValue("$entry_id", entryId);
            insert.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            insert.Parameters.AddWithValue("$is_primary", 1);
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }

        return path;
    }

    public void ClearPersonalPreviewPrimary(long entryId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_personal_previews SET is_primary = 0 WHERE entry_id = $entry_id;";
        command.Parameters.AddWithValue("$entry_id", entryId);
        command.ExecuteNonQuery();
    }

    public void SetPersonalPreviewPrimary(long entryId, long previewId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT 1 FROM library_personal_previews WHERE id = $id AND entry_id = $entry_id LIMIT 1;";
            verify.Parameters.AddWithValue("$id", previewId);
            verify.Parameters.AddWithValue("$entry_id", entryId);
            if (verify.ExecuteScalar() is null)
                throw new InvalidOperationException("That personal preview no longer exists.");
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE library_personal_previews SET is_primary = 0 WHERE entry_id = $entry_id;";
            clear.Parameters.AddWithValue("$entry_id", entryId);
            clear.ExecuteNonQuery();
        }

        using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = "UPDATE library_personal_previews SET is_primary = 1 WHERE id = $id AND entry_id = $entry_id;";
            set.Parameters.AddWithValue("$id", previewId);
            set.Parameters.AddWithValue("$entry_id", entryId);
            set.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void DeletePersonalPreview(long entryId, long previewId)
    {
        using var connection = OpenConnection();
        string? path = null;
        var wasPrimary = false;

        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT path, is_primary FROM library_personal_previews WHERE id = $id AND entry_id = $entry_id;";
            lookup.Parameters.AddWithValue("$id", previewId);
            lookup.Parameters.AddWithValue("$entry_id", entryId);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read())
                return;
            path = reader.GetString(0);
            wasPrimary = reader.GetInt32(1) != 0;
        }

        // Delete the file before removing the row. If Windows refuses the file
        // operation, the Library still knows about it and the user can retry.
        DeleteFileIfPresent(path);

        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM library_personal_previews WHERE id = $id AND entry_id = $entry_id;";
            delete.Parameters.AddWithValue("$id", previewId);
            delete.Parameters.AddWithValue("$entry_id", entryId);
            delete.ExecuteNonQuery();
        }

        if (wasPrimary)
        {
            // Prefer the newest surviving personal preview as a replacement. For
            // entries with an original Glam Card the UI will naturally fall back
            // to that card when no personal previews remain.
            using var promote = connection.CreateCommand();
            promote.Transaction = transaction;
            promote.CommandText = """
                UPDATE library_personal_previews
                SET is_primary = 1
                WHERE id = (
                    SELECT id FROM library_personal_previews
                    WHERE entry_id = $entry_id
                    ORDER BY created_at_utc DESC, id DESC
                    LIMIT 1
                );
                """;
            promote.Parameters.AddWithValue("$entry_id", entryId);
            promote.ExecuteNonQuery();
        }

        transaction.Commit();
        TryDeleteEmptyParentDirectories(path);
    }

    public string AddGeneratedShareCard(long entryId, long? personalPreviewId, ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.IsEmpty)
            throw new InvalidDataException("The generated share-card image is empty.");

        var entry = Get(entryId) ?? throw new InvalidOperationException("The selected Library entry no longer exists.");
        if (personalPreviewId.HasValue && entry.PersonalPreviews.All(preview => preview.Id != personalPreviewId.Value))
            throw new InvalidOperationException("The source personal preview no longer belongs to this Library entry.");

        var entryDirectory = GetOrCreateEntryMediaDirectory(entry);
        var cardsDirectory = Path.Combine(entryDirectory, "share-cards");
        Directory.CreateDirectory(cardsDirectory);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        var path = GetUniquePath(Path.Combine(cardsDirectory, $"share-card-{stamp}.png"));
        File.WriteAllBytes(path, pngBytes.ToArray());

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO library_generated_share_cards (entry_id, personal_preview_id, created_at_utc, path)
                VALUES ($entry_id, $personal_preview_id, $created_at_utc, $path);
                """;
            command.Parameters.AddWithValue("$entry_id", entryId);
            command.Parameters.AddWithValue("$personal_preview_id", personalPreviewId.HasValue ? (object)personalPreviewId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            command.ExecuteNonQuery();
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }

        return path;
    }

    public void DeleteGeneratedShareCard(long entryId, long shareCardId)
    {
        using var connection = OpenConnection();
        string? path = null;

        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT path FROM library_generated_share_cards WHERE id = $id AND entry_id = $entry_id;";
            lookup.Parameters.AddWithValue("$id", shareCardId);
            lookup.Parameters.AddWithValue("$entry_id", entryId);
            path = lookup.ExecuteScalar() as string;
        }

        if (string.IsNullOrWhiteSpace(path))
            return;

        DeleteFileIfPresent(path);

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM library_generated_share_cards WHERE id = $id AND entry_id = $entry_id;";
            delete.Parameters.AddWithValue("$id", shareCardId);
            delete.Parameters.AddWithValue("$entry_id", entryId);
            delete.ExecuteNonQuery();
        }

        TryDeleteEmptyParentDirectories(path);
    }

    public void SetRating(long id, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_entries SET rating = $rating WHERE id = $id;";
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetDisplayTitle(long id, string displayTitle)
    {
        var normalized = NormalizeDisplayTitle(displayTitle);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_entries SET display_title = $display_title WHERE id = $id;";
        command.Parameters.AddWithValue("$display_title", normalized);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("The selected Library entry no longer exists.");
    }

    public void SetNotes(long id, string? notes)
    {
        var normalized = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (normalized is { Length: > 4000 })
            normalized = normalized[..4000];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_entries SET notes = $notes WHERE id = $id;";
        command.Parameters.AddWithValue("$notes", DbValue(normalized));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetTags(long id, IEnumerable<string> tags)
    {
        var normalized = tags
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Length > 48 ? tag[..48] : tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM library_tags WHERE entry_id = $entry_id;";
            delete.Parameters.AddWithValue("$entry_id", id);
            delete.ExecuteNonQuery();
        }

        foreach (var tag in normalized)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO library_tags (entry_id, tag) VALUES ($entry_id, $tag);";
            insert.Parameters.AddWithValue("$entry_id", id);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetWanted(uint itemId, string itemName, bool wanted)
    {
        if (itemId == 0)
            return;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (wanted)
        {
            command.CommandText = """
                INSERT INTO wanted_items (item_id, item_name, added_at_utc)
                VALUES ($item_id, $item_name, $added_at_utc)
                ON CONFLICT(item_id) DO UPDATE SET
                    item_name = excluded.item_name;
                """;
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$item_name", string.IsNullOrWhiteSpace(itemName) ? $"Item #{itemId}" : itemName);
            command.Parameters.AddWithValue("$added_at_utc", DateTime.UtcNow.ToString("O"));
        }
        else
        {
            command.CommandText = "DELETE FROM wanted_items WHERE item_id = $item_id;";
            command.Parameters.AddWithValue("$item_id", itemId);
        }
        command.ExecuteNonQuery();
    }

    public List<WantedItem> GetWantedItems()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                w.item_id,
                w.item_name,
                w.added_at_utc,
                COUNT(DISTINCT p.entry_id) AS used_by
            FROM wanted_items w
            LEFT JOIN library_pieces p ON p.display_item_id = w.item_id
            GROUP BY w.item_id, w.item_name, w.added_at_utc
            ORDER BY w.added_at_utc DESC, w.item_name COLLATE NOCASE ASC;
            """;

        var results = new List<WantedItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var added = DateTime.TryParse(reader.GetString(2), null, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTime.MinValue;
            results.Add(new WantedItem
            {
                ItemId = checked((uint)reader.GetInt64(0)),
                ItemName = reader.GetString(1),
                AddedAtUtc = added,
                UsedByCaptures = checked((int)reader.GetInt64(3)),
            });
        }
        return results;
    }

    public void Delete(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteWithFiles(LibraryEntry entry)
    {
        // Delete sidecars first and the main card last. If Windows refuses to
        // remove one of the optional files, the card/library entry remains and
        // the user can simply retry.
        DeleteFileIfPresent(entry.RawPreviewPath);
        DeleteFileIfPresent(entry.DiagnosticJsonPath);
        DeleteFileIfPresent(entry.AdventurerPlatePath);
        foreach (var sourceImagePath in entry.SourceImagePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            DeleteFileIfPresent(sourceImagePath);
        foreach (var personalPreview in entry.PersonalPreviews)
            DeleteFileIfPresent(personalPreview.Path);
        foreach (var shareCard in entry.GeneratedShareCards)
            DeleteFileIfPresent(shareCard.Path);
        DeleteFileIfPresent(entry.CardPath);
        Delete(entry.Id);

        var cleanupPaths = new List<string>();
        cleanupPaths.AddRange(entry.PersonalPreviews.Select(x => x.Path));
        cleanupPaths.AddRange(entry.GeneratedShareCards.Select(x => x.Path));
        cleanupPaths.AddRange(entry.SourceImagePaths);
        foreach (var optionalPath in new[]
                 {
                     entry.CardPath,
                     entry.RawPreviewPath,
                     entry.DiagnosticJsonPath,
                     entry.AdventurerPlatePath,
                 })
        {
            if (!string.IsNullOrWhiteSpace(optionalPath))
                cleanupPaths.Add(optionalPath);
        }

        foreach (var path in cleanupPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            TryDeleteEmptyParentDirectories(path);
    }

    public string ExportPackage(LibraryEntry entry, string outputFolder)
    {
        if (IsGlamCodePath(entry.CardPath))
            throw new InvalidOperationException("Glam Code entries do not contain a Glam Card image. Share them with Copy Glam Code instead.");
        if (!File.Exists(entry.CardPath))
            throw new FileNotFoundException("The Glam Card PNG no longer exists.", entry.CardPath);
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new DirectoryNotFoundException("The configured GlamSpector output folder is empty.");

        var exportFolder = Path.Combine(outputFolder, "Exports");
        Directory.CreateDirectory(exportFolder);

        var localStamp = entry.CapturedAtUtc == DateTime.MinValue
            ? DateTime.Now
            : entry.CapturedAtUtc.ToLocalTime();
        var stem = $"{MakeSafeFilePart(entry.CharacterName)}_{MakeSafeFilePart(entry.HomeWorld)}_{localStamp:yyyy-MM-dd_HH-mm-ss}";
        var packagePath = GetUniquePath(Path.Combine(exportFolder, stem + ".glamspector.zip"));

        var hasPlate = !string.IsNullOrWhiteSpace(entry.AdventurerPlatePath) && File.Exists(entry.AdventurerPlatePath);
        var manifest = new GlamSpectorPackageManifest
        {
            ExportedAtUtc = DateTime.UtcNow,
            AdventurerPlateFile = hasPlate ? "adventurer-plate.png" : null,
            PortraitSettings = entry.PortraitSettings,
            Snapshot = new GlamourSnapshot
            {
                CapturedAtUtc = entry.CapturedAtUtc,
                CharacterName = entry.CharacterName,
                HomeWorld = entry.HomeWorld,
                FreeCompanyName = entry.FreeCompanyName,
                Pieces = new List<GlamourPiece>(entry.Pieces),
                Facewear = entry.FacewearId != 0 || !string.IsNullOrWhiteSpace(entry.FacewearName)
                    ? new FacewearDiagnostics
                    {
                        GlassesId0 = entry.FacewearId,
                        GlassesName0 = entry.FacewearName,
                        Source = "package",
                    }
                    : null,
            },
        };

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var manifestStream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        var cardEntry = archive.CreateEntry("card.png", CompressionLevel.Optimal);
        using (var source = File.OpenRead(entry.CardPath))
        using (var destination = cardEntry.Open())
            source.CopyTo(destination);

        if (hasPlate)
        {
            var plateEntry = archive.CreateEntry("adventurer-plate.png", CompressionLevel.Optimal);
            using var source = File.OpenRead(entry.AdventurerPlatePath!);
            using var destination = plateEntry.Open();
            source.CopyTo(destination);
        }

        return packagePath;
    }

    public LibraryPackageImportResult ImportSharePackages(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("The configured GlamSpector output folder does not exist.");

        var imported = 0;
        var existingSkipped = 0;
        var failed = 0;

        foreach (var packagePath in Directory.EnumerateFiles(folder, "*.glamspector.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var archive = ZipFile.OpenRead(packagePath);
                var manifestEntry = archive.GetEntry("manifest.json")
                                    ?? throw new InvalidDataException("Package has no manifest.json.");
                GlamSpectorPackageManifest? manifest;
                using (var manifestStream = manifestEntry.Open())
                    manifest = JsonSerializer.Deserialize<GlamSpectorPackageManifest>(manifestStream);

                if (manifest is null || !string.Equals(manifest.Format, "GlamSpector", StringComparison.Ordinal) || manifest.FormatVersion != 1)
                    throw new InvalidDataException("Unsupported GlamSpector package format.");

                var cardEntry = archive.GetEntry(manifest.CardFile)
                                ?? throw new InvalidDataException("Package has no Glam Card image.");

                var packageStem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(packagePath));
                var importRoot = Path.Combine(MediaRoot, "Imported");
                Directory.CreateDirectory(importRoot);
                var entryFolder = Path.Combine(importRoot, MakeSafeFilePart(packageStem));
                Directory.CreateDirectory(entryFolder);
                var cardPath = Path.GetFullPath(Path.Combine(entryFolder, "glam-card.png"));

                if (ContainsCardPath(cardPath))
                {
                    existingSkipped++;
                    continue;
                }

                using (var source = cardEntry.Open())
                using (var destination = File.Create(cardPath))
                    source.CopyTo(destination);

                var entryId = AddCapture(manifest.Snapshot, cardPath, null, null);
                if (manifest.PortraitSettings is not null)
                    SetPortraitSettings(entryId, manifest.PortraitSettings);
                if (!string.IsNullOrWhiteSpace(manifest.AdventurerPlateFile))
                {
                    var plateEntry = archive.GetEntry(manifest.AdventurerPlateFile);
                    if (plateEntry is not null)
                    {
                        var platePath = Path.Combine(entryFolder, "adventurer-plate.png");
                        using (var source = plateEntry.Open())
                        using (var destination = File.Create(platePath))
                            source.CopyTo(destination);
                        SetAdventurerPlatePath(entryId, platePath);
                    }
                }
                imported++;
            }
            catch
            {
                failed++;
            }
        }

        return new LibraryPackageImportResult
        {
            Imported = imported,
            ExistingSkipped = existingSkipped,
            Failed = failed,
        };
    }

    private string GetOrCreateEntryMediaDirectory(LibraryEntry entry)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.CardPath))
            candidates.Add(entry.CardPath);
        candidates.AddRange(entry.SourceImagePaths);

        foreach (var candidate in candidates)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(candidate));
                if (!string.IsNullOrWhiteSpace(directory) && IsPathUnderRoot(directory, MediaRoot))
                    return directory;
            }
            catch
            {
                // Fall through to the legacy per-entry directory below.
            }
        }

        var legacyRoot = Path.Combine(MediaRoot, "Legacy");
        Directory.CreateDirectory(legacyRoot);
        var entryDirectory = Path.Combine(legacyRoot, $"Entry-{entry.Id:D8}");
        Directory.CreateDirectory(entryDirectory);
        return entryDirectory;
    }

    private static bool HasUsableBaseImage(LibraryEntry entry)
    {
        if (!IsImageLessPath(entry.CardPath) && File.Exists(entry.CardPath))
            return true;
        return entry.SourceImagePaths.Any(File.Exists);
    }

    private bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteEmptyParentDirectories(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            var root = Path.GetFullPath(MediaRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrWhiteSpace(directory) &&
                   IsPathUnderRoot(directory, root) &&
                   !string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    break;
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch
        {
            // Empty-directory cleanup is best effort only.
        }
    }

    private static void DeleteFileIfPresent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    }

    private static string GetUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
            return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? ".";
        var extension = Path.GetExtension(desiredPath);
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException("Could not choose a unique export filename.");
    }

    private static string MakeSafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
    }

    private bool ContainsCardPath(string cardPath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM library_entries WHERE card_path = $card_path LIMIT 1;";
        command.Parameters.AddWithValue("$card_path", Path.GetFullPath(cardPath));
        return command.ExecuteScalar() is not null;
    }

    private static GlamourSnapshot ReadSnapshotFromJson(string jsonPath, string cardPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = document.RootElement;

        var fallback = CreateImageOnlySnapshot(cardPath);
        var pieces = new List<GlamourPiece>();

        if (root.TryGetProperty("Pieces", out var piecesElement) && piecesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var pieceElement in piecesElement.EnumerateArray())
            {
                var rawSlotIndex = GetInt32(pieceElement, "RawSlotIndex");
                var slotName = GetString(pieceElement, "SlotName")
                               ?? GetString(pieceElement, "ProvisionalSlotName")
                               ?? SlotNameForIndex(rawSlotIndex);

                pieces.Add(new GlamourPiece
                {
                    RawSlotIndex = rawSlotIndex,
                    SlotName = slotName,
                    EquippedItemId = GetUInt32(pieceElement, "EquippedItemId"),
                    GlamourItemId = GetUInt32(pieceElement, "GlamourItemId"),
                    DisplayItemId = GetUInt32(pieceElement, "DisplayItemId"),
                    DisplayItemName = GetString(pieceElement, "DisplayItemName") ?? "Unknown Item",
                    Stain1Id = GetByte(pieceElement, "Stain1Id"),
                    Stain1Name = GetString(pieceElement, "Stain1Name"),
                    Stain2Id = GetByte(pieceElement, "Stain2Id"),
                    Stain2Name = GetString(pieceElement, "Stain2Name"),
                });
            }
        }

        FacewearDiagnostics? facewear = null;
        if (root.TryGetProperty("Facewear", out var facewearElement) && facewearElement.ValueKind == JsonValueKind.Object)
        {
            var id0 = GetUInt16(facewearElement, "GlassesId0");
            var id1 = GetUInt16(facewearElement, "GlassesId1");
            var name0 = GetString(facewearElement, "GlassesName0");
            var name1 = GetString(facewearElement, "GlassesName1");
            var displayName = GetString(facewearElement, "DisplayName");

            if (id0 != 0 || id1 != 0 || !string.IsNullOrWhiteSpace(displayName))
            {
                if (id0 != 0 && string.IsNullOrWhiteSpace(name0))
                    name0 = displayName;
                else if (id1 != 0 && string.IsNullOrWhiteSpace(name1))
                    name1 = displayName;

                facewear = new FacewearDiagnostics
                {
                    GlassesId0 = id0,
                    GlassesName0 = name0,
                    GlassesId1 = id1,
                    GlassesName1 = name1,
                    CharaViewState = GetUInt32(facewearElement, "CharaViewState"),
                    CharacterLoaded = GetBool(facewearElement, "CharacterLoaded"),
                    Source = GetString(facewearElement, "Source") ?? "imported",
                };
            }
        }

        var capturedAtUtc = GetDateTime(root, "CapturedAtUtc") ?? fallback.CapturedAtUtc;
        return new GlamourSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            EntityId = GetUInt32(root, "EntityId"),
            CharacterName = GetString(root, "CharacterName") ?? fallback.CharacterName,
            HomeWorld = GetString(root, "HomeWorld") ?? fallback.HomeWorld,
            FreeCompanyName = GetString(root, "FreeCompanyName"),
            Pieces = pieces,
            Facewear = facewear,
        };
    }

    private static GlamourSnapshot CreateImageOnlySnapshot(string cardPath)
    {
        var fileStem = Path.GetFileNameWithoutExtension(cardPath);
        var characterName = "Unknown Character";
        var homeWorld = "Unknown World";
        DateTime capturedAtUtc;

        var match = CaptureFileName.Match(fileStem);
        if (match.Success)
        {
            characterName = match.Groups["character"].Value;
            homeWorld = match.Groups["world"].Value;

            var localStamp = $"{match.Groups["date"].Value}_{match.Groups["time"].Value}";
            if (DateTime.TryParseExact(
                    localStamp,
                    "yyyy-MM-dd_HH-mm-ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var localTime))
            {
                capturedAtUtc = localTime.ToUniversalTime();
            }
            else
            {
                capturedAtUtc = File.GetLastWriteTimeUtc(cardPath);
            }
        }
        else
        {
            capturedAtUtc = File.GetLastWriteTimeUtc(cardPath);
        }

        return new GlamourSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            CharacterName = characterName,
            HomeWorld = homeWorld,
            Pieces = [],
        };
    }

    private static string SlotNameForIndex(int rawSlotIndex) => rawSlotIndex switch
    {
        0 => "Main Hand",
        1 => "Off Hand",
        2 => "Head",
        3 => "Body",
        4 => "Hands",
        6 => "Legs",
        7 => "Feet",
        8 => "Earrings",
        9 => "Necklace",
        10 => "Bracelets",
        11 => "Right Ring",
        12 => "Left Ring",
        _ => $"Slot {rawSlotIndex}",
    };

    private static void PopulatePieces(SqliteConnection connection, IReadOnlyCollection<LibraryEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var byId = entries.ToDictionary(x => x.Id);
        var ids = entries.Select(x => x.Id).ToArray();

        // Keep comfortably below SQLite's parameter limit even for very large
        // libraries / duplicate scans.
        const int batchSize = 400;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var parameter = $"$entry{i}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, batch[i]);
            }

            command.CommandText = $"""
                SELECT
                    entry_id,
                    raw_slot_index,
                    slot_name,
                    equipped_item_id,
                    glamour_item_id,
                    display_item_id,
                    display_item_name,
                    stain1_id,
                    stain1_name,
                    stain2_id,
                    stain2_name
                FROM library_pieces
                WHERE entry_id IN ({string.Join(",", parameters)})
                ORDER BY entry_id, raw_slot_index;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entryId = reader.GetInt64(0);
                if (!byId.TryGetValue(entryId, out var entry))
                    continue;

                entry.Pieces.Add(new GlamourPiece
                {
                    RawSlotIndex = reader.GetInt32(1),
                    SlotName = reader.GetString(2),
                    EquippedItemId = checked((uint)reader.GetInt64(3)),
                    GlamourItemId = checked((uint)reader.GetInt64(4)),
                    DisplayItemId = checked((uint)reader.GetInt64(5)),
                    DisplayItemName = reader.GetString(6),
                    Stain1Id = checked((byte)reader.GetInt32(7)),
                    Stain1Name = GetNullableString(reader, 8),
                    Stain2Id = checked((byte)reader.GetInt32(9)),
                    Stain2Name = GetNullableString(reader, 10),
                });
            }
        }
    }

    private static void PopulateTags(SqliteConnection connection, IReadOnlyCollection<LibraryEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var byId = entries.ToDictionary(x => x.Id);
        var ids = entries.Select(x => x.Id).ToArray();
        const int batchSize = 400;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var parameter = $"$tagEntry{i}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, batch[i]);
            }

            command.CommandText = $"SELECT entry_id, tag FROM library_tags WHERE entry_id IN ({string.Join(",", parameters)}) ORDER BY tag COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (byId.TryGetValue(reader.GetInt64(0), out var entry))
                    entry.Tags.Add(reader.GetString(1));
            }
        }
    }

    private static void PopulateSourceImages(SqliteConnection connection, IReadOnlyCollection<LibraryEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var byId = entries.ToDictionary(x => x.Id);
        var ids = entries.Select(x => x.Id).ToArray();
        const int batchSize = 400;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var parameter = $"$sourceEntry{i}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, batch[i]);
            }

            command.CommandText = $"SELECT entry_id, path FROM library_source_images WHERE entry_id IN ({string.Join(",", parameters)}) ORDER BY entry_id, ordinal;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (byId.TryGetValue(reader.GetInt64(0), out var entry))
                    entry.SourceImagePaths.Add(reader.GetString(1));
            }
        }
    }

    private static void PopulatePersonalPreviews(SqliteConnection connection, IReadOnlyCollection<LibraryEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var byId = entries.ToDictionary(x => x.Id);
        var ids = entries.Select(x => x.Id).ToArray();
        const int batchSize = 400;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var parameter = $"$previewEntry{i}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, batch[i]);
            }

            command.CommandText = $"""
                SELECT id, entry_id, created_at_utc, path, is_primary
                FROM library_personal_previews
                WHERE entry_id IN ({string.Join(",", parameters)})
                ORDER BY entry_id, created_at_utc DESC, id DESC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entryId = reader.GetInt64(1);
                if (!byId.TryGetValue(entryId, out var entry))
                    continue;

                var created = DateTime.TryParse(reader.GetString(2), null, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                entry.PersonalPreviews.Add(new PersonalPreview
                {
                    Id = reader.GetInt64(0),
                    EntryId = entryId,
                    CreatedAtUtc = created,
                    Path = reader.GetString(3),
                    IsPrimary = reader.GetInt32(4) != 0,
                });
            }
        }
    }

    private static void PopulateGeneratedShareCards(SqliteConnection connection, IReadOnlyCollection<LibraryEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var byId = entries.ToDictionary(x => x.Id);
        var ids = entries.Select(x => x.Id).ToArray();
        const int batchSize = 400;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var parameter = $"$shareCardEntry{i}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, batch[i]);
            }

            command.CommandText = $"""
                SELECT id, entry_id, personal_preview_id, created_at_utc, path
                FROM library_generated_share_cards
                WHERE entry_id IN ({string.Join(",", parameters)})
                ORDER BY entry_id, created_at_utc DESC, id DESC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entryId = reader.GetInt64(1);
                if (!byId.TryGetValue(entryId, out var entry))
                    continue;

                var created = DateTime.TryParse(reader.GetString(3), null, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                entry.GeneratedShareCards.Add(new GeneratedShareCard
                {
                    Id = reader.GetInt64(0),
                    EntryId = entryId,
                    PersonalPreviewId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    CreatedAtUtc = created,
                    Path = reader.GetString(4),
                });
            }
        }
    }

    private static void PopulateMediaSizes(IEnumerable<LibraryEntry> entries)
    {
        foreach (var entry in entries)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPath(paths, entry.CardPath);
            AddPath(paths, entry.RawPreviewPath);
            AddPath(paths, entry.DiagnosticJsonPath);
            AddPath(paths, entry.AdventurerPlatePath);
            foreach (var source in entry.SourceImagePaths)
                AddPath(paths, source);
            foreach (var preview in entry.PersonalPreviews)
                AddPath(paths, preview.Path);
            foreach (var shareCard in entry.GeneratedShareCards)
                AddPath(paths, shareCard.Path);

            long total = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        total += new FileInfo(path).Length;
                }
                catch
                {
                    // File-size sorting is informational. A temporarily locked or
                    // inaccessible file should not make the whole Library fail.
                }
            }
            entry.TotalMediaBytes = total;
        }
    }

    private static void AddPath(HashSet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            paths.Add(Path.GetFullPath(path));
        }
        catch
        {
            paths.Add(path);
        }
    }

    private static LibraryEntry ReadEntry(SqliteDataReader reader)
    {
        var captured = DateTime.TryParse(
            reader.GetString(1),
            null,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.MinValue;

        var characterName = reader.GetString(2);
        var homeWorld = reader.GetString(3);
        var sourceKind = reader.FieldCount > 14 ? GetNullableString(reader, 14) : null;
        var sourceTitle = reader.FieldCount > 16 ? GetNullableString(reader, 16) : null;
        var storedDisplayTitle = reader.FieldCount > 18 ? GetNullableString(reader, 18) : null;
        var displayTitle = !string.IsNullOrWhiteSpace(storedDisplayTitle)
            ? storedDisplayTitle.Trim()
            : string.Equals(sourceKind, "EorzeaCollection", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sourceTitle)
                ? sourceTitle.Trim()
                : CreateDefaultDisplayTitle(characterName, homeWorld);

        return new LibraryEntry
        {
            Id = reader.GetInt64(0),
            CapturedAtUtc = captured,
            DisplayTitle = displayTitle,
            CharacterName = characterName,
            HomeWorld = homeWorld,
            CardPath = reader.GetString(4),
            RawPreviewPath = GetNullableString(reader, 5),
            DiagnosticJsonPath = GetNullableString(reader, 6),
            FacewearId = checked((ushort)reader.GetInt32(7)),
            FacewearName = GetNullableString(reader, 8),
            FreeCompanyName = GetNullableString(reader, 9),
            AdventurerPlatePath = GetNullableString(reader, 10),
            PortraitSettings = DeserializePortraitSettings(GetNullableString(reader, 11)),
            Rating = reader.FieldCount > 12 && !reader.IsDBNull(12) ? Math.Clamp(reader.GetInt32(12), 0, 5) : 0,
            Notes = reader.FieldCount > 13 ? GetNullableString(reader, 13) : null,
            SourceKind = sourceKind,
            SourceUrl = reader.FieldCount > 15 ? GetNullableString(reader, 15) : null,
            SourceTitle = sourceTitle,
            SourceCreator = reader.FieldCount > 17 ? GetNullableString(reader, 17) : null,
        };
    }

    private static string CreateDefaultDisplayTitle(string? characterName, string? homeWorld)
    {
        var character = string.IsNullOrWhiteSpace(characterName) ? "Unknown Character" : characterName.Trim();
        var world = string.IsNullOrWhiteSpace(homeWorld) ? "Unknown World" : homeWorld.Trim();
        return CreateInitialDisplayTitle($"{character} @ {world}", "Untitled glamour");
    }

    private static string CreateInitialDisplayTitle(string? displayTitle, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(displayTitle) ? fallback : displayTitle.Trim();
        return normalized.Length > 200 ? normalized[..200].TrimEnd() : normalized;
    }

    private static string NormalizeDisplayTitle(string? displayTitle)
    {
        var normalized = displayTitle?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Library title cannot be empty or whitespace.", nameof(displayTitle));
        if (normalized.Length > 200)
            throw new ArgumentException("Library title cannot exceed 200 characters.", nameof(displayTitle));
        return normalized;
    }

    private static PortraitSettingsSnapshot? DeserializePortraitSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PortraitSettingsSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static uint GetUInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetUInt32(out var value) ? value : 0;

    private static ushort GetUInt16(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetUInt32(out var value))
            return 0;
        return value <= ushort.MaxValue ? (ushort)value : (ushort)0;
    }

    private static byte GetByte(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var value))
            return 0;
        return value is >= byte.MinValue and <= byte.MaxValue ? (byte)value : (byte)0;
    }

    private static bool GetBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False) &&
        property.GetBoolean();

    private static DateTime? GetDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        return property.TryGetDateTime(out var value) ? value : null;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
