# ShareService calls FileProvider.getUriForFile through JNI, which R8 cannot see.
-keep class androidx.core.content.FileProvider { public static android.net.Uri getUriForFile(android.content.Context, java.lang.String, java.io.File); }
-keep class com.nexus.share.NexusFileProvider
