#ifndef ENGINE_BRIDGE_H
#define ENGINE_BRIDGE_H

#ifdef ENGINEBRIDGE_EXPORTS
#define ENGINEBRIDGE_API __declspec(dllexport)
#else
#define ENGINEBRIDGE_API __declspec(dllimport)
#endif

#include <string>
#include <functional>

typedef void (*ProgressCallback)(void* userData, const char* fileName, long long bytesDownloaded, long long totalBytes, int activeConnections, double speedBps);
typedef void (*StatusCallback)(void* userData, const char* status, const char* message);
typedef void (*LogCallback)(void* userData, const char* message);

extern "C" {
    ENGINEBRIDGE_API bool Engine_Initialize();
    ENGINEBRIDGE_API void Engine_Shutdown();
    ENGINEBRIDGE_API void Engine_SetCallbacks(ProgressCallback progressCb, StatusCallback statusCb, LogCallback logCb, void* userData);
    ENGINEBRIDGE_API bool Engine_StartDownload(const char* url, const char* destinationFolder, int maxConnections, long long maxSpeedBytesPerSec, int timeoutSeconds);
    ENGINEBRIDGE_API bool Engine_PauseDownload();
    ENGINEBRIDGE_API bool Engine_ResumeDownload();
    ENGINEBRIDGE_API bool Engine_CancelDownload();
    ENGINEBRIDGE_API bool Engine_IsRunning();
}

#endif // ENGINE_BRIDGE_H
