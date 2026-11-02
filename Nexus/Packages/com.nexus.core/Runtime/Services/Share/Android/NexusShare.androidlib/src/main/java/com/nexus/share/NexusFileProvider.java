package com.nexus.share;

import androidx.core.content.FileProvider;

/**
 * ShareService's provider (#134). A class of its own, so the manifest merger keeps it apart from a game's or
 * another library's androidx FileProvider instead of merging the two declarations into one.
 */
public class NexusFileProvider extends FileProvider {
}
