#include "EngineBridge.h"
#include <windows.h>
#include <string>
#include <thread>
#include <atomic>
#include <mutex>
#include <chrono>
#include <curl/curl.h>

static ProgressCallback g_progressCb = nullptr;
static StatusCallback g_statusCb = nullptr;
static LogCallback g_logCb = nullptr;
static void* g_userData = nullptr;

static std::atomic<bool> g_running(false);
static std::atomic<bool> g_paused(false);
static std::atomic<bool> g_cancel(false);
static std::thread g_workerThread;

static std::string g_destinationFolder;
static std::string g_url;
static int g_maxConnections = 4;
static long long g_maxSpeed = 0;

struct DownloadState {
    std::string url;
    std::string filename;
    std::string folder;
    long long fileSize = 0;
    long long downloaded = 0;
    std::atomic<bool> cancel{false};
    std::atomic<bool> paused{false};
    std::atomic<bool> running{false};
    std::mutex mutex;
};

static DownloadState g_state;

static size_t WriteCallback(void* contents, size_t size, size_t nmemb, FILE* stream) {
    return fwrite(contents, size, nmemb, stream);
}

static size_t HeaderCallback(char* buffer, size_t size, size_t nitems, void* userdata) {
    size_t totalSize = size * nitems;
    std::string header(buffer, totalSize);
    
    // Parse Content-Disposition header for filename
    size_t pos = header.find("Content-Disposition:");
    if (pos != std::string::npos) {
        size_t filenamePos = header.find("filename=", pos);
        if (filenamePos != std::string::npos) {
            filenamePos += 9;
            if (filenamePos < header.length() && header[filenamePos] == '"') {
                filenamePos++;
            }
            size_t endPos = header.find_first_of("\";\r\n", filenamePos);
            if (endPos != std::string::npos) {
                std::string filename = header.substr(filenamePos, endPos - filenamePos);
                std::string decoded;
                decoded.reserve(filename.length());
                for (size_t i = 0; i < filename.length(); ++i) {
                    if (filename[i] == '%' && i + 2 < filename.length()) {
                        char hex[3] = { filename[i + 1], filename[i + 2], 0 };
                        decoded.push_back(static_cast<char>(strtol(hex, nullptr, 16)));
                        i += 2;
                    } else {
                        decoded.push_back(filename[i]);
                    }
                }
                if (!decoded.empty()) {
                    auto* state = static_cast<DownloadState*>(userdata);
                    state->filename = decoded;
                }
            }
        }
    }
    
    return totalSize;
}

static void LogMessage(const char* msg) {
    if (g_logCb) g_logCb(g_userData, msg);
}

ENGINEBRIDGE_API bool Engine_Initialize() {
    CURLcode res = curl_global_init(CURL_GLOBAL_DEFAULT);
    if (res != CURLE_OK) return false;
    return true;
}

ENGINEBRIDGE_API void Engine_Shutdown() {
    curl_global_cleanup();
}

ENGINEBRIDGE_API void Engine_SetCallbacks(ProgressCallback progressCb, StatusCallback statusCb, LogCallback logCb, void* userData) {
    g_progressCb = progressCb;
    g_statusCb = statusCb;
    g_logCb = logCb;
    g_userData = userData;
}

struct CurlProgressData {
    long long total;
    long long downloaded;
    std::chrono::steady_clock::time_point startTime;
    std::chrono::steady_clock::time_point lastUpdate;
    std::atomic<bool> cancel{false};
};

static int CurlProgressFn(void* clientp, curl_off_t dltotal, curl_off_t dlnow, curl_off_t ultotal, curl_off_t ulnow) {
    auto* data = static_cast<CurlProgressData*>(clientp);
    data->total = dltotal;
    data->downloaded = dlnow;
    
    // Throttle progress updates to every 500ms to avoid UI freezing
    auto now = std::chrono::steady_clock::now();
    auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - data->lastUpdate).count();
    if (elapsed >= 500 && g_progressCb && !data->cancel.load()) {
        data->lastUpdate = now;
        double totalElapsed = std::chrono::duration<double>(now - data->startTime).count();
        double speed = totalElapsed > 0 ? (dlnow / totalElapsed) : 0;
        g_progressCb(g_userData, g_state.filename.c_str(), dlnow, dltotal, 1, speed);
    }
    
    return data->cancel.load() ? 1 : 0;
}

static std::string ExtractFilenameFromUrl(const std::string& url) {
    size_t queryPos = url.find('?');
    std::string cleanUrl = (queryPos != std::string::npos) ? url.substr(0, queryPos) : url;
    
    size_t fragmentPos = cleanUrl.find('#');
    if (fragmentPos != std::string::npos) {
        cleanUrl = cleanUrl.substr(0, fragmentPos);
    }
    
    size_t lastSlash = cleanUrl.find_last_of("/\\");
    std::string filename = (lastSlash != std::string::npos) ? cleanUrl.substr(lastSlash + 1) : cleanUrl;
    
    std::string decoded;
    decoded.reserve(filename.length());
    for (size_t i = 0; i < filename.length(); ++i) {
        if (filename[i] == '%' && i + 2 < filename.length()) {
            char hex[3] = { filename[i + 1], filename[i + 2], 0 };
            decoded.push_back(static_cast<char>(strtol(hex, nullptr, 16)));
            i += 2;
        } else if (filename[i] == '+') {
            decoded.push_back(' ');
        } else {
            decoded.push_back(filename[i]);
        }
    }
    
    std::string valid;
    for (char c : decoded) {
        if (c != '<' && c != '>' && c != ':' && c != '"' && c != '/' && c != '\\' && c != '|' && c != '?' && c != '*') {
            valid.push_back(c);
        }
    }
    
    if (valid.empty() || valid == "." || valid == "..") {
        valid = "download_" + std::to_string(GetTickCount()) + ".bin";
    }
    
    return valid;
}

static std::string ExtractReferer(const std::string& url) {
    size_t schemeEnd = url.find("://");
    if (schemeEnd == std::string::npos) return "";
    size_t pathStart = url.find('/', schemeEnd + 3);
    if (pathStart == std::string::npos) return url;
    return url.substr(0, pathStart);
}

ENGINEBRIDGE_API bool Engine_StartDownload(const char* url, const char* destinationFolder, int maxConnections, long long maxSpeedBytesPerSec, int timeoutSeconds) {
    if (g_running) {
        LogMessage("Download already in progress.");
        return false;
    }

    g_url = url;
    g_destinationFolder = destinationFolder;
    g_maxConnections = maxConnections > 0 ? maxConnections : 4;
    g_maxSpeed = maxSpeedBytesPerSec;
    g_cancel = false;
    g_paused = false;
    g_running = true;

    g_state.url = url;
    g_state.folder = destinationFolder;
    g_state.cancel = false;
    g_state.paused = false;
    g_state.running = true;
    g_state.downloaded = 0;

    std::string filename = ExtractFilenameFromUrl(url);
    g_state.filename = filename;

    CreateDirectoryA(destinationFolder, nullptr);

    g_workerThread = std::thread([]() {
        CURL* curl = curl_easy_init();
        if (!curl) {
            if (g_statusCb) g_statusCb(g_userData, "Error", "Failed to initialize CURL.");
            g_running = false;
            return;
        }

        std::string fullPath = g_destinationFolder + "\\" + g_state.filename;
        FILE* fp = fopen(fullPath.c_str(), "wb");
        if (!fp) {
            if (g_statusCb) g_statusCb(g_userData, "Error", "Failed to create output file.");
            curl_easy_cleanup(curl);
            g_running = false;
            return;
        }

        if (g_statusCb) g_statusCb(g_userData, "Connecting", "Connecting to server...");

        struct curl_slist* headers = nullptr;
        headers = curl_slist_append(headers, "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        headers = curl_slist_append(headers, "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        headers = curl_slist_append(headers, "Accept-Language: en-US,en;q=0.9");
        headers = curl_slist_append(headers, "Accept-Encoding: identity");
        headers = curl_slist_append(headers, "Connection: keep-alive");
        headers = curl_slist_append(headers, "Upgrade-Insecure-Requests: 1");
        headers = curl_slist_append(headers, "Sec-Fetch-Dest: document");
        headers = curl_slist_append(headers, "Sec-Fetch-Mode: navigate");
        headers = curl_slist_append(headers, "Sec-Fetch-Site: none");
        headers = curl_slist_append(headers, "Sec-Fetch-User: ?1");
        headers = curl_slist_append(headers, "Cache-Control: max-age: 0");

        std::string referer = ExtractReferer(g_url);
        if (!referer.empty()) {
            std::string refererHeader = "Referer: " + referer;
            headers = curl_slist_append(headers, refererHeader.c_str());
        }

        curl_easy_setopt(curl, CURLOPT_URL, g_url.c_str());
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, WriteCallback);
        curl_easy_setopt(curl, CURLOPT_WRITEDATA, fp);
        curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, HeaderCallback);
        curl_easy_setopt(curl, CURLOPT_HEADERDATA, &g_state);
        curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
        curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);
        curl_easy_setopt(curl, CURLOPT_MAXREDIRS, 10L);
        curl_easy_setopt(curl, CURLOPT_TIMEOUT, 60L);
        curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, 30L);
        curl_easy_setopt(curl, CURLOPT_NOPROGRESS, 0L);
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 0L);
        curl_easy_setopt(curl, CURLOPT_ACCEPT_ENCODING, "identity");
        curl_easy_setopt(curl, CURLOPT_HTTP_VERSION, CURL_HTTP_VERSION_1_1);
        curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
        curl_easy_setopt(curl, CURLOPT_BUFFERSIZE, 16384L);

        if (g_maxSpeed > 0) {
            curl_easy_setopt(curl, CURLOPT_MAX_RECV_SPEED_LARGE, (curl_off_t)g_maxSpeed);
        }

        CurlProgressData progressData = {};
        progressData.startTime = std::chrono::steady_clock::now();
        progressData.lastUpdate = std::chrono::steady_clock::now();
        progressData.cancel = false;
        curl_easy_setopt(curl, CURLOPT_XFERINFOFUNCTION, CurlProgressFn);
        curl_easy_setopt(curl, CURLOPT_XFERINFODATA, &progressData);

        int maxRetries = 3;
        int retryCount = 0;
        CURLcode res = CURLE_OK;

        while (retryCount < maxRetries) {
            if (g_cancel.load()) break;

            res = curl_easy_perform(curl);

            if (res == CURLE_OK) break;

            if (res == CURLE_OPERATION_TIMEDOUT || res == CURLE_COULDNT_CONNECT ||
                res == CURLE_COULDNT_RESOLVE_HOST || res == CURLE_RECV_ERROR) {
                retryCount++;
                if (retryCount < maxRetries) {
                    if (g_statusCb) {
                        char retryMsg[128];
                        snprintf(retryMsg, sizeof(retryMsg), "Retrying... (attempt %d/%d)", retryCount + 1, maxRetries);
                        g_statusCb(g_userData, "Retrying", retryMsg);
                    }
                    Sleep(2000 * retryCount);
                    continue;
                }
            }
            break;
        }

        fclose(fp);
        curl_slist_free_all(headers);

        if (g_cancel.load()) {
            DeleteFileA(fullPath.c_str());
            if (g_statusCb) g_statusCb(g_userData, "Cancelled", "Download cancelled by user.");
        }
        else if (res != CURLE_OK) {
            char errMsg[256];
            snprintf(errMsg, sizeof(errMsg), "Download failed: %s", curl_easy_strerror(res));
            if (g_statusCb) g_statusCb(g_userData, "Error", errMsg);
            DeleteFileA(fullPath.c_str());
        }
        else {
            std::string finalFilename = g_state.filename;
            std::string oldPath = fullPath;
            fullPath = g_destinationFolder + "\\" + finalFilename;
            
            if (finalFilename != ExtractFilenameFromUrl(g_url.c_str())) {
                MoveFileA(oldPath.c_str(), fullPath.c_str());
            }

            curl_off_t speed;
            curl_easy_getinfo(curl, CURLINFO_SPEED_DOWNLOAD_T, &speed);
            if (g_progressCb) {
                g_progressCb(g_userData, finalFilename.c_str(), progressData.downloaded, progressData.total, 1, (double)speed);
            }
            if (g_statusCb) g_statusCb(g_userData, "Completed", "Download completed successfully.");
        }

        curl_easy_cleanup(curl);
        g_state.running = false;
        g_running = false;
    });

    return true;
}

ENGINEBRIDGE_API bool Engine_PauseDownload() {
    if (!g_running || g_paused) return false;
    g_paused = true;
    g_state.paused = true;
    if (g_statusCb) g_statusCb(g_userData, "Paused", "Download paused.");
    return true;
}

ENGINEBRIDGE_API bool Engine_ResumeDownload() {
    if (!g_running || !g_paused) return false;
    g_paused = false;
    g_state.paused = false;
    if (g_statusCb) g_statusCb(g_userData, "Resumed", "Download resumed.");
    return true;
}

ENGINEBRIDGE_API bool Engine_CancelDownload() {
    if (!g_running) return false;
    g_cancel = true;
    g_state.cancel = true;
    return true;
}

ENGINEBRIDGE_API bool Engine_IsRunning() {
    return g_running;
}
