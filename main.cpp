#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shobjidl.h>
#include "resource.h"
#include "build_config.h"

#include <curl/curl.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <cwctype>
#include <cstdint>
#include <cstdio>
#include <ctime>
#include <cstring>
#include <filesystem>
#include <functional>
#include <deque>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <map>
#include <new>
#include <mutex>
#include <stdexcept>
#include <sstream>
#include <shared_mutex>
#include <string>
#include <thread>
#include <type_traits>
#include <vector>


namespace {

constexpr unsigned int kDefaultSegments = 16;
constexpr unsigned int kMaxSegments = 128;
constexpr size_t kDiskWriteBufferSize = 1024 * 1024;
constexpr uint32_t kLedgerBlockSize = 1024 * 1024;
constexpr uint64_t kMaximumLedgerBytes = 64ULL * 1024 * 1024;
constexpr uint32_t kLedgerVersion = 1;
constexpr size_t kLedgerMaxPathBytes = 65536;
constexpr size_t kLedgerMaxEtagBytes = 1024;
constexpr size_t kLedgerMaxLastModifiedBytes = 256;
// A larger libcurl receive buffer reduces callback overhead on fast links while
// keeping per-worker memory use bounded (about 64 MiB at 128 workers).
constexpr long kCurlReceiveBufferSize = 512L * 1024L;
std::atomic_bool gConsoleCancelled{false};
std::atomic_bool gDownloadPaused{false};

using StatusCallback = std::function<void(const std::wstring&)>;

struct CurlGlobal final {
    CurlGlobal() : code(curl_global_init(CURL_GLOBAL_DEFAULT)) {}
    ~CurlGlobal() { if (code == CURLE_OK) curl_global_cleanup(); }
    CURLcode code;
};

struct HeadInfo {
    bool accepts_ranges = false;
    std::string filename;
    std::string etag;
    std::string lastModified;
};

bool checkCurl(CURLcode result, const char* operation);

std::string trim(std::string value) {
    const auto first = std::find_if_not(value.begin(), value.end(),
        [](unsigned char c) { return std::isspace(c) != 0; });
    const auto last = std::find_if_not(value.rbegin(), value.rend(),
        [](unsigned char c) { return std::isspace(c) != 0; }).base();
    if (first >= last) return {};
    return std::string(first, last);
}

std::string lower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

std::wstring utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0);
    if (size <= 0) return std::wstring(value.begin(), value.end());
    std::wstring result(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), size);
    return result;
}

std::string wideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) return {};
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
        result.data(), size, nullptr, nullptr);
    return result;
}

int hexValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

std::string percentDecode(const std::string& value) {
    std::string decoded;
    decoded.reserve(value.size());
    for (size_t i = 0; i < value.size(); ++i) {
        if (value[i] == '%' && i + 2 < value.size()) {
            const int high = hexValue(value[i + 1]);
            const int low = hexValue(value[i + 2]);
            if (high >= 0 && low >= 0) {
                decoded.push_back(static_cast<char>((high << 4) | low));
                i += 2;
                continue;
            }
        }
        decoded.push_back(value[i]);
    }
    return decoded;
}

std::string safeFilename(std::string value) {
    value = trim(value);
    if (value.size() >= 2 && value.front() == '"' && value.back() == '"')
        value = value.substr(1, value.size() - 2);
    value = percentDecode(value);
    const auto separator = value.find_last_of("/\\");
    if (separator != std::string::npos) value = value.substr(separator + 1);
    for (char& c : value) {
        const unsigned char byte = static_cast<unsigned char>(c);
        if (byte < 32 || c == '<' || c == '>' || c == ':' || c == '"' ||
            c == '/' || c == '\\' || c == '|' || c == '?' || c == '*') c = '_';
    }
    while (!value.empty() && (value.back() == ' ' || value.back() == '.')) value.pop_back();
    if (value.empty() || value == "." || value == "..") return "download";
    const std::string stem = lower(value.substr(0, value.find('.')));
    if (stem == "con" || stem == "prn" || stem == "aux" || stem == "nul" ||
        (stem.size() == 4 && (stem.rfind("com", 0) == 0 || stem.rfind("lpt", 0) == 0) &&
         stem[3] >= '1' && stem[3] <= '9')) value.insert(value.begin(), '_');
    return value;
}

std::string filenameFromUrl(const std::string& url) {
    const size_t end = url.find_first_of("?#");
    const std::string withoutQuery = url.substr(0, end);
    const size_t slash = withoutQuery.find_last_of('/');
    return safeFilename(slash == std::string::npos ? withoutQuery : withoutQuery.substr(slash + 1));
}

std::string filenameFromDisposition(const std::string& value) {
    const std::string folded = lower(value);
    size_t key = folded.find("filename*=");
    bool extended = key != std::string::npos;
    size_t keyLength = extended ? 10 : 0;
    if (!extended) {
        key = folded.find("filename=");
        keyLength = 9;
    }
    if (key == std::string::npos) return {};
    size_t start = key + keyLength;
    size_t end = value.find(';', start);
    std::string filename = trim(value.substr(start, end == std::string::npos ? end : end - start));
    if (extended) {
        const size_t encoding = filename.find("''");
        if (encoding != std::string::npos) filename.erase(0, encoding + 2);
    }
    return safeFilename(filename);
}

size_t headHeaderCallback(char* data, size_t size, size_t count, void* user) {
    if (size != 0 && count > (std::numeric_limits<size_t>::max)() / size) return 0;
    const size_t bytes = size * count;
    auto* info = static_cast<HeadInfo*>(user);
    const std::string line(data, bytes);
    if (lower(line).rfind("http/", 0) == 0) {
        info->accepts_ranges = false;
        info->filename.clear();
        info->etag.clear();
        info->lastModified.clear();
    }
    const auto colon = line.find(':');
    if (colon != std::string::npos) {
        const std::string header = lower(trim(line.substr(0, colon)));
        if (header == "accept-ranges") info->accepts_ranges = lower(trim(line.substr(colon + 1))) == "bytes";
        else if (header == "content-disposition") info->filename = filenameFromDisposition(trim(line.substr(colon + 1)));
        else if (header == "etag") info->etag = trim(line.substr(colon + 1));
        else if (header == "last-modified") info->lastModified = trim(line.substr(colon + 1));
    }
    return bytes;
}

bool checkCurl(CURLcode result, const char* operation) {
    if (result == CURLE_OK) return true;
    std::cerr << operation << " failed: " << curl_easy_strerror(result) << '\n';
    return false;
}

long preferredHttpVersion() {
    const curl_version_info_data* runtime = curl_version_info(CURLVERSION_NOW);
    if (runtime && (runtime->features & CURL_VERSION_HTTP3)) return CURL_HTTP_VERSION_3;
    if (runtime && (runtime->features & CURL_VERSION_HTTP2)) return CURL_HTTP_VERSION_2TLS;
    return CURL_HTTP_VERSION_1_1;
}

struct RangeTask {
    uint64_t first = 0;
    uint64_t last = 0;
    unsigned int homeThread = 0;
};

struct CompletedRangeQueue {
    std::mutex mutex;
    std::deque<RangeTask> ranges;
    void push(const RangeTask& range) {
        std::lock_guard<std::mutex> lock(mutex);
        ranges.push_back(range);
    }
    std::deque<RangeTask> takeAll() {
        std::lock_guard<std::mutex> lock(mutex);
        std::deque<RangeTask> result;
        result.swap(ranges);
        return result;
    }
};

struct WorkerProgress {
    std::atomic<uint64_t> bytes{0};
    std::atomic<uint64_t> taskBytes{0};
    std::atomic<uint64_t> taskSize{0};
    std::atomic<unsigned int> homeThread{0};
    std::atomic<int> state{0}; // 0 waiting, 1 downloading, 2 helping, 3 done
};

using ProgressCallback = std::function<void(uint64_t, uint64_t, unsigned int, unsigned int,
                                             const std::vector<WorkerProgress>&)>;

// Lightweight local hill-climber: probe one extra connection, retain it only
// when observed throughput improves, and back off when disk writes queue up.
class AdaptiveController final {
public:
    explicit AdaptiveController(unsigned int maximum)
        : maximum_(std::max(1u, maximum)), limit_(std::min(4u, maximum_)),
          lastSampleTime_(std::chrono::steady_clock::now()), nextProbe_(lastSampleTime_ + std::chrono::seconds(5)) {}

    bool acquire(const std::atomic_bool& stop, const std::atomic_bool* cancel) {
        std::unique_lock<std::mutex> lock(mutex_);
        while (active_ >= limit_ && !stop.load() && !(cancel && cancel->load()))
            changed_.wait_for(lock, std::chrono::milliseconds(100));
        if (stop.load() || (cancel && cancel->load())) return false;
        ++active_;
        return true;
    }

    void release() {
        { std::lock_guard<std::mutex> lock(mutex_); if (active_ > 0) --active_; }
        changed_.notify_all();
    }

    unsigned int limit() const { return visibleLimit_.load(); }

    void observe(uint64_t receivedBytes, unsigned int pendingWrites,
                 std::chrono::steady_clock::time_point now) {
        if (now - lastSampleTime_ < std::chrono::seconds(5)) return;
        const double seconds = std::chrono::duration<double>(now - lastSampleTime_).count();
        const double rate = seconds > 0.0
            ? static_cast<double>(receivedBytes - lastSampleBytes_) / seconds : 0.0;
        lastSampleBytes_ = receivedBytes;
        lastSampleTime_ = now;

        if (pendingWrites >= 48) {
            setLimit(limit() > 1 ? limit() - 1 : 1);
            trialPending_ = false;
            nextProbe_ = now + std::chrono::seconds(10);
            return;
        }
        if (trialPending_) {
            // Keep a new connection only if it delivers a measurable gain.
            if (rate < trialBaselineRate_ * 1.05) setLimit(trialPreviousLimit_);
            trialPending_ = false;
            nextProbe_ = now + std::chrono::seconds(8);
        } else if (now >= nextProbe_ && limit() < maximum_ && rate > 0.0) {
            trialBaselineRate_ = rate;
            trialPreviousLimit_ = limit();
            setLimit(trialPreviousLimit_ + 1);
            trialPending_ = true;
            nextProbe_ = now + std::chrono::seconds(5);
        }
    }

private:
    const unsigned int maximum_;
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    unsigned int active_ = 0;
    unsigned int limit_;
    std::atomic<unsigned int> visibleLimit_{limit_};
    uint64_t lastSampleBytes_ = 0;
    std::chrono::steady_clock::time_point lastSampleTime_;
    std::chrono::steady_clock::time_point nextProbe_;
    double trialBaselineRate_ = 0.0;
    unsigned int trialPreviousLimit_ = 0;
    bool trialPending_ = false;

    void setLimit(unsigned int value) {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            limit_ = std::clamp(value, 1u, maximum_);
            visibleLimit_.store(limit_);
        }
        changed_.notify_all();
    }
};

struct Segment {
    const std::string* url = nullptr;
    const std::wstring* path = nullptr;
    const std::string* ifRangeValidator = nullptr;
    uint64_t first = 0;
    uint64_t last = 0;
    uint64_t expected = 0;
    std::atomic<uint64_t> written{0};
    uint64_t contiguousWritten = 0;
    uint64_t queued = 0;
    uint64_t received = 0;
    std::vector<char> writeBuffer;
    std::atomic_bool write_failed{false};
    std::atomic<unsigned int> pendingWrites{0};
    std::mutex completionMutex;
    std::condition_variable completionChanged;
    std::map<uint64_t, uint64_t> completedRanges;
    class AsyncDiskWriter* diskWriter = nullptr;
    std::atomic_bool* cancel = nullptr;
    std::atomic_bool* paused = nullptr;
    std::atomic_bool* stop = nullptr;
    std::atomic<uint64_t>* totalWritten = nullptr;
    std::atomic<uint64_t>* totalReceived = nullptr;
    WorkerProgress* progress = nullptr;
    AdaptiveController* controller = nullptr;
    long connectTimeoutSeconds = 90;
    curl_off_t maxReceiveBytesPerSecond = 0;
};

struct BackgroundIoScope final {
    BackgroundIoScope() : active(SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) != FALSE) {}
    ~BackgroundIoScope() {
        if (active) SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
    }
    bool active;
};

// Each request owns its buffer until IOCP reports completion. At most 64 requests
// can be outstanding, so a slow disk applies backpressure instead of growing RAM.
class AsyncDiskWriter final {
    struct WriteContext {
        OVERLAPPED overlapped{};
        Segment* segment = nullptr;
        std::vector<char> data;
        uint64_t offset = 0;
        size_t completed = 0;
    };

public:
    explicit AsyncDiskWriter(const std::wstring& path, unsigned int workerCount) {
        file_ = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, nullptr);
        if (file_ == INVALID_HANDLE_VALUE)
            throw std::runtime_error("Could not open output file for asynchronous writes (Windows error " +
                                     std::to_string(GetLastError()) + ").");
        port_ = CreateIoCompletionPort(file_, nullptr, 0, 0);
        if (!port_) {
            const DWORD error = GetLastError(); CloseHandle(file_); file_ = INVALID_HANDLE_VALUE;
            throw std::runtime_error("Could not create I/O completion port (Windows error " +
                                     std::to_string(error) + ").");
        }
        const unsigned int count = std::max(1u, std::min(4u, workerCount));
        try {
            for (unsigned int i = 0; i < count; ++i) threads_.emplace_back(&AsyncDiskWriter::completionLoop, this);
        } catch (...) {
            for (size_t i = 0; i < threads_.size(); ++i) PostQueuedCompletionStatus(port_, 0, 0, nullptr);
            for (auto& thread : threads_) if (thread.joinable()) thread.join();
            CloseHandle(port_); CloseHandle(file_); port_ = nullptr; file_ = INVALID_HANDLE_VALUE;
            throw;
        }
    }

    ~AsyncDiskWriter() {
        close();
    }

    void close() {
        if (closed_) return;
        for (size_t i = 0; i < threads_.size(); ++i) PostQueuedCompletionStatus(port_, 0, 0, nullptr);
        for (auto& thread : threads_) if (thread.joinable()) thread.join();
        if (port_) CloseHandle(port_);
        if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
        port_ = nullptr;
        file_ = INVALID_HANDLE_VALUE;
        closed_ = true;
    }

    bool submit(Segment& segment) {
        if (segment.writeBuffer.empty()) return true;
        std::shared_lock<std::shared_mutex> submissionLock(submissionBarrier_);
        {
            std::unique_lock<std::mutex> lock(capacityMutex_);
            capacityChanged_.wait(lock, [&] { return outstanding_ < kMaxOutstanding; });
            ++outstanding_;
        }
        auto* context = new (std::nothrow) WriteContext;
        if (!context) { releaseCapacity(); segment.write_failed.store(true); return false; }
        context->segment = &segment;
        context->offset = segment.first + segment.queued;
        const size_t submittedSize = segment.writeBuffer.size();
        std::vector<char> replacementBuffer;
        try { replacementBuffer.reserve(kDiskWriteBufferSize); }
        catch (...) { delete context; releaseCapacity(); segment.write_failed.store(true); return false; }
        context->data.swap(segment.writeBuffer);
        segment.writeBuffer.swap(replacementBuffer);
        segment.pendingWrites.fetch_add(1);
        if (!issue(context)) return false;
        segment.queued += submittedSize;
        segment.writeBuffer.clear();
        return true;
    }

    void wait(Segment& segment) {
        std::unique_lock<std::mutex> lock(segment.completionMutex);
        segment.completionChanged.wait(lock, [&] { return segment.pendingWrites.load() == 0; });
    }

    unsigned int outstanding() {
        std::lock_guard<std::mutex> lock(capacityMutex_);
        return outstanding_;
    }

    bool flushData() {
        std::unique_lock<std::shared_mutex> barrier(submissionBarrier_);
        {
            std::unique_lock<std::mutex> lock(capacityMutex_);
            capacityChanged_.wait(lock, [&] { return outstanding_ == 0; });
        }
        return file_ != INVALID_HANDLE_VALUE && FlushFileBuffers(file_) != FALSE;
    }

private:
    static constexpr unsigned int kMaxOutstanding = 64;
    HANDLE file_ = INVALID_HANDLE_VALUE;
    HANDLE port_ = nullptr;
    std::vector<std::thread> threads_;
    std::mutex capacityMutex_;
    std::condition_variable capacityChanged_;
    std::shared_mutex submissionBarrier_;
    unsigned int outstanding_ = 0;
    bool closed_ = false;

    void releaseCapacity() {
        { std::lock_guard<std::mutex> lock(capacityMutex_); --outstanding_; }
        capacityChanged_.notify_all();
    }

    bool issue(WriteContext* context) {
        std::memset(&context->overlapped, 0, sizeof(context->overlapped));
        const uint64_t offset = context->offset + context->completed;
        context->overlapped.Offset = static_cast<DWORD>(offset & 0xffffffffu);
        context->overlapped.OffsetHigh = static_cast<DWORD>(offset >> 32);
        const DWORD length = static_cast<DWORD>(std::min<size_t>(context->data.size() - context->completed,
            static_cast<size_t>((std::numeric_limits<DWORD>::max)())));
        DWORD transferred = 0;
        const BOOL ok = WriteFile(file_, context->data.data() + context->completed, length,
                                  &transferred, &context->overlapped);
        if (!ok && GetLastError() != ERROR_IO_PENDING) {
            context->segment->write_failed.store(true);
            finish(context, false, 0);
            return false;
        }
        return true;
    }

    void completionLoop() {
        for (;;) {
            DWORD transferred = 0;
            ULONG_PTR key = 0;
            OVERLAPPED* overlapped = nullptr;
            const BOOL ok = GetQueuedCompletionStatus(port_, &transferred, &key, &overlapped, INFINITE);
            if (!overlapped) { if (key == 0) return; continue; }
            auto* context = reinterpret_cast<WriteContext*>(overlapped);
            if (!ok || transferred == 0) {
                context->segment->write_failed.store(true);
                finish(context, false, 0);
                continue;
            }
            context->completed += transferred;
            context->segment->written.fetch_add(transferred);
            if (context->segment->totalWritten) context->segment->totalWritten->fetch_add(transferred);
            if (context->segment->progress) {
                context->segment->progress->bytes.fetch_add(transferred);
                context->segment->progress->taskBytes.store(context->segment->written.load());
            }
            if (context->completed < context->data.size()) {
                if (!issue(context)) continue;
            } else finish(context, true, context->data.size());
        }
    }

    void finish(WriteContext* context, bool success, size_t length) {
        Segment& segment = *context->segment;
        if (success) {
            std::lock_guard<std::mutex> lock(segment.completionMutex);
            segment.completedRanges.emplace(context->offset, length);
            for (;;) {
                auto it = segment.completedRanges.upper_bound(segment.first + segment.contiguousWritten);
                if (it != segment.completedRanges.begin()) --it;
                if (it == segment.completedRanges.end() || it->first > segment.first + segment.contiguousWritten ||
                    it->first + it->second <= segment.first + segment.contiguousWritten) break;
                segment.contiguousWritten = it->first + it->second - segment.first;
                segment.completedRanges.erase(it);
            }
        }
        delete context;
        segment.pendingWrites.fetch_sub(1);
        segment.completionChanged.notify_all();
        releaseCapacity();
    }
};

bool flushSegmentBuffer(Segment& segment) {
    if (segment.writeBuffer.empty()) return true;
    return segment.diskWriter && segment.diskWriter->submit(segment);
}

int transferProgressCallback(void* user, curl_off_t, curl_off_t, curl_off_t, curl_off_t) {
    const auto* segment = static_cast<const Segment*>(user);
    return (segment->cancel && segment->cancel->load()) || (segment->stop && segment->stop->load()) ||
           (segment->paused && segment->paused->load()) ? 1 : 0;
}

int headProgressCallback(void* user, curl_off_t, curl_off_t, curl_off_t, curl_off_t) {
    const auto* cancel = static_cast<const std::atomic_bool*>(user);
    return cancel && cancel->load() ? 1 : 0;
}

size_t fileWriteCallback(char* data, size_t size, size_t count, void* user) {
    if (size != 0 && count > (std::numeric_limits<size_t>::max)() / size) return 0;
    const size_t bytes = size * count;
    auto* segment = static_cast<Segment*>(user);
    if (segment->received > segment->expected || bytes > segment->expected - segment->received) {
        segment->write_failed = true; // A server that ignored Range must not overwrite another segment.
        return 0;
    }

    try {
        segment->writeBuffer.insert(segment->writeBuffer.end(), data, data + bytes);
    } catch (...) {
        segment->write_failed = true;
        return 0;
    }
    segment->received += bytes;
    if (segment->totalReceived) segment->totalReceived->fetch_add(bytes);
    if (segment->writeBuffer.size() >= kDiskWriteBufferSize && !flushSegmentBuffer(*segment)) return 0;
    return bytes;
}

bool downloadRange(Segment& segment, CURL* curl, std::mutex& logMutex, bool terminalUi) {
    constexpr unsigned int kMaxAttempts = 8;
    CURLcode result = CURLE_OK;
    long status = 0;
    unsigned int attempt = 0;
    while (attempt < kMaxAttempts) {
        while (segment.paused && segment.paused->load()) {
            if ((segment.cancel && segment.cancel->load()) || (segment.stop && segment.stop->load())) return false;
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        if (segment.write_failed.load() || (segment.cancel && segment.cancel->load()) ||
            (segment.stop && segment.stop->load())) return false;
        ++attempt;
        // A retry resumes at the last contiguous committed byte, so the
        // callback's range-overflow guard must use that same logical position.
        segment.received = segment.contiguousWritten;
        const uint64_t nextByte = segment.first + segment.contiguousWritten;
        const std::string range = std::to_string(nextByte) + "-" + std::to_string(segment.last);
        result = curl_easy_setopt(curl, CURLOPT_RANGE, range.c_str());
        if (result != CURLE_OK) break;
        result = curl_easy_perform(curl);
        if (!flushSegmentBuffer(segment)) result = CURLE_WRITE_ERROR;
        segment.diskWriter->wait(segment);
        status = 0;
        const CURLcode infoResult = curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &status);
        if (infoResult != CURLE_OK && result == CURLE_OK) result = infoResult;
        if (status == 206 && segment.contiguousWritten == segment.expected) return true;
        if (segment.paused && segment.paused->load()) {
            while (segment.paused->load()) {
                if ((segment.cancel && segment.cancel->load()) || (segment.stop && segment.stop->load())) return false;
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }
            if ((segment.cancel && segment.cancel->load()) || (segment.stop && segment.stop->load())) return false;
            --attempt; // A user pause is not a failed network attempt.
            continue;
        }
        const bool cancelled = (segment.cancel && segment.cancel->load()) ||
                               (segment.stop && segment.stop->load());
        const bool retryable = !cancelled && !segment.write_failed.load() && attempt < kMaxAttempts &&
            (result != CURLE_OK || status == 429 || status >= 500);
        if (!retryable) break;
        if (!terminalUi) {
            std::lock_guard<std::mutex> lock(logMutex);
            std::cerr << "Range [" << segment.first << '-' << segment.last << "] retry "
                      << attempt << '/' << (kMaxAttempts - 1) << " after "
                      << segment.contiguousWritten << '/' << segment.expected << " bytes.\n";
        }
        for (unsigned int tick = 0; tick < (1u << (attempt - 1)) * 10; ++tick) {
            if ((segment.cancel && segment.cancel->load()) || (segment.stop && segment.stop->load())) return false;
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
    }
    if (!(segment.cancel && segment.cancel->load()) && !(segment.stop && segment.stop->load())) {
        std::lock_guard<std::mutex> lock(logMutex);
        std::cerr << "Range [" << segment.first << '-' << segment.last << "] failed: ";
        if (segment.write_failed.load()) std::cerr << "disk write failed or range overflowed";
        else if (result != CURLE_OK) std::cerr << curl_easy_strerror(result);
        else if (status != 206) std::cerr << "server returned HTTP " << status << " instead of 206";
        else std::cerr << "received " << segment.contiguousWritten << " of " << segment.expected << " bytes";
        std::cerr << '\n';
    }
    return false;
}

void downloadSegment(unsigned int workerIndex, std::deque<RangeTask>& tasks,
                     std::mutex& queueMutex, Segment& segment, WorkerProgress& progress,
                     std::atomic<unsigned int>& finishedWorkers, std::atomic_bool& stop,
                     std::atomic_bool& failed, std::mutex& logMutex, bool terminalUi,
                     AsyncDiskWriter& diskWriter, CompletedRangeQueue& completedRanges) {
    auto finish = [&]() { progress.state.store(3); finishedWorkers.fetch_add(1); };
    segment.diskWriter = &diskWriter;
    CURL* curl = curl_easy_init();
    if (!curl) {
        std::lock_guard<std::mutex> lock(logMutex);
        std::cerr << "T" << workerIndex + 1 << " could not create a curl context.\n";
        failed.store(true);
        stop.store(true);
        finish();
        return;
    }
    try {
        segment.writeBuffer.reserve(kDiskWriteBufferSize);
    } catch (...) {
        std::lock_guard<std::mutex> lock(logMutex);
        std::cerr << "T" << workerIndex + 1 << " could not allocate its disk buffer.\n";
        curl_easy_cleanup(curl);
        failed.store(true);
        stop.store(true);
        finish();
        return;
    }

    bool configured = true;
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_URL, segment.url->c_str()), "Setting URL");
    if (std::filesystem::exists("curl-ca-bundle.crt"))
        configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_CAINFO, "curl-ca-bundle.crt"), "Setting bundled TLS certificate authorities");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, fileWriteCallback), "Setting write callback");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_WRITEDATA, &segment), "Setting write context");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L), "Enabling redirects");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_HTTP_VERSION, preferredHttpVersion()), "Selecting HTTP protocol");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, segment.connectTimeoutSeconds), "Setting connect timeout");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_BUFFERSIZE, kCurlReceiveBufferSize), "Setting receive buffer size");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_LOW_SPEED_LIMIT, 64L), "Setting minimum transfer speed");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_LOW_SPEED_TIME, 240L), "Setting low-speed timeout");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L), "Disabling curl signals");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_NOPROGRESS, 0L), "Enabling cancellation checks");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_XFERINFOFUNCTION, transferProgressCallback), "Setting cancellation callback");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_XFERINFODATA, &segment), "Setting cancellation context");
    curl_slist* requestHeaders = nullptr;
    if (segment.ifRangeValidator && !segment.ifRangeValidator->empty()) {
        const std::string ifRange = "If-Range: " + *segment.ifRangeValidator;
        requestHeaders = curl_slist_append(nullptr, ifRange.c_str());
        if (!requestHeaders) {
            std::cerr << "T" << workerIndex + 1 << " could not allocate the range validator header.\n";
            configured = false;
        } else {
            configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_HTTPHEADER, requestHeaders),
                                    "Setting the If-Range validator");
        }
    }
    if (!configured) { failed.store(true); stop.store(true); }

    while (configured && !stop.load() && !(segment.cancel && segment.cancel->load())) {
        progress.state.store(0);
        if (!segment.controller || !segment.controller->acquire(stop, segment.cancel)) break;
        RangeTask task;
        {
            std::lock_guard<std::mutex> lock(queueMutex);
            if (tasks.empty()) {
                segment.controller->release();
                break;
            }
            task = tasks.front();
            tasks.pop_front();
        }
        segment.first = task.first;
        segment.last = task.last;
        segment.expected = task.last - task.first + 1;
        segment.written.store(0);
        segment.contiguousWritten = 0;
        segment.queued = 0;
        segment.pendingWrites.store(0);
        segment.completedRanges.clear();
        segment.received = 0;
        segment.writeBuffer.clear();
        segment.write_failed.store(false);
        progress.taskBytes.store(0);
        progress.taskSize.store(segment.expected);
        progress.homeThread.store(task.homeThread);
        progress.state.store(task.homeThread == workerIndex ? 1 : 2);
        if (segment.maxReceiveBytesPerSecond > 0) {
            const uint64_t activeLimit = std::max(1u, segment.controller->limit());
            const auto perConnectionLimit = static_cast<curl_off_t>(std::max<uint64_t>(
                1, segment.maxReceiveBytesPerSecond / activeLimit));
            if (!checkCurl(curl_easy_setopt(curl, CURLOPT_MAX_RECV_SPEED_LARGE, perConnectionLimit),
                           "Updating adaptive per-connection receive limit")) {
                segment.controller->release();
                failed.store(true);
                stop.store(true);
                break;
            }
        }
        const bool taskSucceeded = downloadRange(segment, curl, logMutex, terminalUi);
        segment.controller->release();
        if (!taskSucceeded) {
            if (!(segment.cancel && segment.cancel->load()) && !stop.load()) failed.store(true);
            stop.store(true);
            break;
        }
        completedRanges.push(task);
    }

    if (!segment.writeBuffer.empty() && !segment.write_failed.load()) flushSegmentBuffer(segment);
    diskWriter.wait(segment);
    if (requestHeaders) curl_slist_free_all(requestHeaders);
    curl_easy_cleanup(curl);
    finish();
}

bool getRemoteFileInfo(const std::string& url, uint64_t& length, std::string& filename,
                      std::string& etag, std::string& lastModified,
                      std::atomic_bool* cancel = nullptr, long connectTimeoutSeconds = 30) {
    CURL* curl = curl_easy_init();
    if (!curl) {
        std::cerr << "Could not create curl context for HTTP HEAD request.\n";
        return false;
    }
    HeadInfo headers;
    bool configured = true;
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_URL, url.c_str()), "Setting HEAD URL");
    if (std::filesystem::exists("curl-ca-bundle.crt"))
        configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_CAINFO, "curl-ca-bundle.crt"), "Setting bundled TLS certificate authorities");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_NOBODY, 1L), "Configuring HTTP HEAD");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L), "Enabling redirects");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_HTTP_VERSION, preferredHttpVersion()), "Selecting HTTP protocol");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, headHeaderCallback), "Setting header callback");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_HEADERDATA, &headers), "Setting header context");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, connectTimeoutSeconds), "Setting connect timeout");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_NOPROGRESS, 0L), "Enabling cancellation checks");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_XFERINFOFUNCTION, headProgressCallback), "Setting cancellation callback");
    configured &= checkCurl(curl_easy_setopt(curl, CURLOPT_XFERINFODATA, cancel), "Setting cancellation state");
    CURLcode result = configured ? curl_easy_perform(curl) : CURLE_FAILED_INIT;
    long status = 0;
    curl_off_t contentLength = -1;
    if (result == CURLE_OK) result = curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &status);
    if (result == CURLE_OK) result = curl_easy_getinfo(curl, CURLINFO_CONTENT_LENGTH_DOWNLOAD_T, &contentLength);
    if (result != CURLE_OK) std::cerr << "HTTP HEAD request failed: " << curl_easy_strerror(result) << '\n';
    else if (status < 200 || status >= 300) std::cerr << "HTTP HEAD returned status " << status << ".\n";
    else if (contentLength <= 0) std::cerr << "Could not determine a positive Content-Length from HTTP HEAD.\n";
    else if (!headers.accepts_ranges) std::cerr << "Server did not advertise Accept-Ranges: bytes.\n";
    const bool ok = result == CURLE_OK && status >= 200 && status < 300 &&
                    contentLength > 0 && headers.accepts_ranges;
    if (ok) {
        length = static_cast<uint64_t>(contentLength);
        filename = headers.filename.empty() ? filenameFromUrl(url) : headers.filename;
        etag = headers.etag;
        lastModified = headers.lastModified;
    }
    curl_easy_cleanup(curl);
    return ok;
}

bool preallocateFile(const std::wstring& path, uint64_t size, bool resumeExisting = false) {
    BackgroundIoScope backgroundIo;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
        resumeExisting ? OPEN_EXISTING : CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::cerr << "Could not create output file (Windows error " << GetLastError() << ").\n";
        return false;
    }
    bool ok = false;
    if (resumeExisting) {
        LARGE_INTEGER existing{};
        ok = GetFileSizeEx(file, &existing) && existing.QuadPart == static_cast<LONGLONG>(size);
        if (!ok) std::cerr << "Could not resume partial file because its size does not match the ledger.\n";
    } else {
        LARGE_INTEGER end{};
        end.QuadPart = static_cast<LONGLONG>(size);
        ok = SetFilePointerEx(file, end, nullptr, FILE_BEGIN) && SetEndOfFile(file);
        if (ok) ok = FlushFileBuffers(file) != FALSE;
        if (!ok) std::cerr << "Could not pre-allocate output file (Windows error " << GetLastError() << ").\n";
    }
    CloseHandle(file);
    return ok;
}

struct DownloadLedger {
    uint64_t fileSize = 0;
    std::string destination;
    std::string etag;
    std::string lastModified;
    std::vector<uint8_t> bitmap;
};

uint64_t ledgerBlockCount(uint64_t fileSize) {
    return fileSize / kLedgerBlockSize + (fileSize % kLedgerBlockSize != 0);
}

uint64_t ledgerBitmapBytes(uint64_t blockCount) {
    return blockCount / 8 + (blockCount % 8 != 0);
}

bool ledgerBit(const std::vector<uint8_t>& bitmap, uint64_t block) {
    const uint64_t byte = block / 8;
    return byte < bitmap.size() && (bitmap[static_cast<size_t>(byte)] & (uint8_t{1} << (block % 8))) != 0;
}

void setLedgerBit(std::vector<uint8_t>& bitmap, uint64_t block) {
    bitmap[static_cast<size_t>(block / 8)] |= static_cast<uint8_t>(uint8_t{1} << (block % 8));
}

void appendLe16(std::vector<uint8_t>& bytes, uint16_t value) {
    bytes.push_back(static_cast<uint8_t>(value));
    bytes.push_back(static_cast<uint8_t>(value >> 8));
}

void appendLe32(std::vector<uint8_t>& bytes, uint32_t value) {
    for (unsigned int i = 0; i < 4; ++i) bytes.push_back(static_cast<uint8_t>(value >> (i * 8)));
}

void appendLe64(std::vector<uint8_t>& bytes, uint64_t value) {
    for (unsigned int i = 0; i < 8; ++i) bytes.push_back(static_cast<uint8_t>(value >> (i * 8)));
}

template <typename UInt>
bool readLe(const std::vector<uint8_t>& bytes, size_t& offset, UInt& value, size_t limit) {
    static_assert(std::is_unsigned<UInt>::value, "readLe requires an unsigned integer");
    if (offset > limit || sizeof(UInt) > limit - offset) return false;
    value = 0;
    for (size_t i = 0; i < sizeof(UInt); ++i)
        value |= static_cast<UInt>(bytes[offset++]) << (i * 8);
    return true;
}

uint32_t ledgerCrc32(const uint8_t* data, size_t size) {
    uint32_t crc = 0xffffffffu;
    for (size_t i = 0; i < size; ++i) {
        crc ^= data[i];
        for (unsigned int bit = 0; bit < 8; ++bit)
            crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1u)));
    }
    return ~crc;
}

bool validLedgerText(const std::string& value, size_t maximum) {
    return value.size() <= maximum && value.find('\0') == std::string::npos &&
           value.find('\r') == std::string::npos && value.find('\n') == std::string::npos;
}

bool usableResumeValidator(const std::string& etag, const std::string& lastModified) {
    const bool strongEtag = !etag.empty() && etag.rfind("W/", 0) != 0;
    return (strongEtag && validLedgerText(etag, kLedgerMaxEtagBytes)) ||
           (!lastModified.empty() && validLedgerText(lastModified, kLedgerMaxLastModifiedBytes));
}

bool ledgerMatches(const DownloadLedger& ledger, uint64_t fileSize, const std::string& destination,
                   const std::string& etag, const std::string& lastModified) {
    if (ledger.fileSize != fileSize || ledger.destination != destination ||
        !usableResumeValidator(etag, lastModified)) return false;
    if (!ledger.etag.empty() && ledger.etag.rfind("W/", 0) != 0)
        return ledger.etag == etag;
    return !ledger.lastModified.empty() && ledger.lastModified == lastModified;
}

bool serializeLedger(const DownloadLedger& ledger, std::vector<uint8_t>& bytes) {
    const uint64_t blockCount = ledgerBlockCount(ledger.fileSize);
    const uint64_t expectedBitmapBytes = ledgerBitmapBytes(blockCount);
    if (ledger.fileSize == 0 || ledger.bitmap.size() != expectedBitmapBytes ||
        expectedBitmapBytes > kMaximumLedgerBytes ||
        !validLedgerText(ledger.destination, kLedgerMaxPathBytes) || ledger.destination.empty() ||
        !validLedgerText(ledger.etag, kLedgerMaxEtagBytes) ||
        !validLedgerText(ledger.lastModified, kLedgerMaxLastModifiedBytes)) return false;
    try {
        bytes.clear();
        bytes.reserve(static_cast<size_t>(44 + ledger.destination.size() + ledger.etag.size() +
                                         ledger.lastModified.size() + expectedBitmapBytes + 4));
        bytes.insert(bytes.end(), {'T', 'I', 'D', 'M'});
        appendLe32(bytes, kLedgerVersion);
        appendLe64(bytes, ledger.fileSize);
        appendLe32(bytes, kLedgerBlockSize);
        appendLe64(bytes, blockCount);
        appendLe32(bytes, static_cast<uint32_t>(ledger.destination.size()));
        appendLe16(bytes, static_cast<uint16_t>(ledger.etag.size()));
        appendLe16(bytes, static_cast<uint16_t>(ledger.lastModified.size()));
        appendLe64(bytes, expectedBitmapBytes);
        bytes.insert(bytes.end(), ledger.destination.begin(), ledger.destination.end());
        bytes.insert(bytes.end(), ledger.etag.begin(), ledger.etag.end());
        bytes.insert(bytes.end(), ledger.lastModified.begin(), ledger.lastModified.end());
        bytes.insert(bytes.end(), ledger.bitmap.begin(), ledger.bitmap.end());
        appendLe32(bytes, ledgerCrc32(bytes.data(), bytes.size()));
        return bytes.size() <= kMaximumLedgerBytes;
    } catch (...) {
        bytes.clear();
        return false;
    }
}

bool saveLedgerAtomic(const std::wstring& ledgerPath, const DownloadLedger& ledger) {
    std::vector<uint8_t> bytes;
    if (!serializeLedger(ledger, bytes)) return false;
    const std::wstring tempPath = ledgerPath + L".tmp";
    HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    size_t offset = 0;
    bool ok = true;
    while (offset < bytes.size()) {
        const DWORD count = static_cast<DWORD>(std::min<size_t>(bytes.size() - offset,
            static_cast<size_t>((std::numeric_limits<DWORD>::max)())));
        DWORD written = 0;
        if (!WriteFile(file, bytes.data() + offset, count, &written, nullptr) || written == 0) {
            ok = false;
            break;
        }
        offset += written;
    }
    if (ok) ok = FlushFileBuffers(file) != FALSE;
    if (!CloseHandle(file)) ok = false;
    if (ok) ok = MoveFileExW(tempPath.c_str(), ledgerPath.c_str(),
        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
    if (!ok) DeleteFileW(tempPath.c_str());
    return ok;
}

bool loadLedger(const std::wstring& ledgerPath, DownloadLedger& ledger) {
    std::ifstream file(std::filesystem::path(ledgerPath), std::ios::binary | std::ios::ate);
    if (!file) return false;
    const std::streamoff end = file.tellg();
    constexpr size_t kFixedLength = 44;
    if (end < static_cast<std::streamoff>(kFixedLength + 4) ||
        static_cast<uint64_t>(end) > kMaximumLedgerBytes) return false;
    std::vector<uint8_t> bytes(static_cast<size_t>(end));
    file.seekg(0, std::ios::beg);
    if (!file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()))) return false;
    if (std::memcmp(bytes.data(), "TIDM", 4) != 0) return false;
    const size_t crcOffset = bytes.size() - 4;
    size_t crcReadOffset = crcOffset;
    uint32_t storedCrc = 0;
    if (!readLe(bytes, crcReadOffset, storedCrc, bytes.size()) ||
        storedCrc != ledgerCrc32(bytes.data(), crcOffset)) return false;

    size_t offset = 4;
    uint32_t version = 0, blockSize = 0, pathLength = 0;
    uint64_t fileSize = 0, blockCount = 0, bitmapLength = 0;
    uint16_t etagLength = 0, modifiedLength = 0;
    if (!readLe(bytes, offset, version, crcOffset) || !readLe(bytes, offset, fileSize, crcOffset) ||
        !readLe(bytes, offset, blockSize, crcOffset) || !readLe(bytes, offset, blockCount, crcOffset) ||
        !readLe(bytes, offset, pathLength, crcOffset) || !readLe(bytes, offset, etagLength, crcOffset) ||
        !readLe(bytes, offset, modifiedLength, crcOffset) || !readLe(bytes, offset, bitmapLength, crcOffset)) return false;
    if (version != kLedgerVersion || fileSize == 0 || blockSize != kLedgerBlockSize ||
        blockCount != ledgerBlockCount(fileSize) || bitmapLength != ledgerBitmapBytes(blockCount) ||
        bitmapLength > kMaximumLedgerBytes || pathLength > kLedgerMaxPathBytes ||
        etagLength > kLedgerMaxEtagBytes || modifiedLength > kLedgerMaxLastModifiedBytes) return false;
    const uint64_t payloadLength = static_cast<uint64_t>(pathLength) + etagLength + modifiedLength + bitmapLength;
    if (payloadLength != crcOffset - offset) return false;
    DownloadLedger loaded;
    loaded.fileSize = fileSize;
    loaded.destination.assign(reinterpret_cast<const char*>(bytes.data() + offset), pathLength);
    offset += pathLength;
    loaded.etag.assign(reinterpret_cast<const char*>(bytes.data() + offset), etagLength);
    offset += etagLength;
    loaded.lastModified.assign(reinterpret_cast<const char*>(bytes.data() + offset), modifiedLength);
    offset += modifiedLength;
    loaded.bitmap.assign(bytes.begin() + offset, bytes.begin() + offset + static_cast<size_t>(bitmapLength));
    if (!validLedgerText(loaded.destination, kLedgerMaxPathBytes) || loaded.destination.empty() ||
        !validLedgerText(loaded.etag, kLedgerMaxEtagBytes) ||
        !validLedgerText(loaded.lastModified, kLedgerMaxLastModifiedBytes)) return false;
    ledger = std::move(loaded);
    return true;
}

class DownloadFileLock final {
public:
    explicit DownloadFileLock(const std::wstring& path) : path_(path) {
        handle_ = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                              OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    }
    ~DownloadFileLock() {
        if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_);
        if (handle_ != INVALID_HANDLE_VALUE) DeleteFileW(path_.c_str());
    }
    bool acquired() const { return handle_ != INVALID_HANDLE_VALUE; }
private:
    std::wstring path_;
    HANDLE handle_ = INVALID_HANDLE_VALUE;
};

} // namespace

std::string progressBar(uint64_t current, uint64_t total, unsigned int width) {
    const double fraction = total == 0 ? 0.0 : std::min(1.0, static_cast<double>(current) / total);
    const unsigned int filled = static_cast<unsigned int>(fraction * width);
    return "[" + std::string(filled, '#') + std::string(width - filled, ' ') + "]";
}

std::string etaText(uint64_t total, uint64_t downloaded, double elapsedSeconds) {
    if (downloaded == 0 || elapsedSeconds < 0.1 || downloaded >= total) return downloaded >= total ? "0m 0s" : "calculating...";
    const double bytesPerSecond = downloaded / elapsedSeconds;
    if (bytesPerSecond <= 0.0) return "calculating...";
    const uint64_t seconds = static_cast<uint64_t>((total - downloaded) / bytesPerSecond);
    if (seconds < 60) return std::to_string(seconds) + "sec";
    return std::to_string(seconds / 60) + "m " + std::to_string(seconds % 60) + "s";
}

std::string megabytes(uint64_t bytes) {
    std::ostringstream out;
    out << std::fixed << std::setprecision(2) << (static_cast<double>(bytes) / (1024.0 * 1024.0)) << "MB";
    return out.str();
}

void pollConsolePauseKeys() {
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    if (input == nullptr || input == INVALID_HANDLE_VALUE) return;
    DWORD pending = 0;
    if (!GetNumberOfConsoleInputEvents(input, &pending) || pending == 0) return;
    std::vector<INPUT_RECORD> records(std::min<DWORD>(pending, 64));
    DWORD read = 0;
    if (!ReadConsoleInputW(input, records.data(), static_cast<DWORD>(records.size()), &read)) return;
    for (DWORD i = 0; i < read; ++i) {
        if (records[i].EventType != KEY_EVENT || !records[i].Event.KeyEvent.bKeyDown) continue;
        const KEY_EVENT_RECORD& key = records[i].Event.KeyEvent;
        const bool controlDown = (key.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;
        if (!controlDown) continue;
        if (key.wVirtualKeyCode == 'P') gDownloadPaused.store(true);
        else if (key.wVirtualKeyCode == 'R') gDownloadPaused.store(false);
    }
}

void renderDownloadFrame(const std::string& filename, uint64_t total, const std::atomic<uint64_t>& downloaded,
                         const std::vector<WorkerProgress>& progress, unsigned int workerCount,
                         unsigned int finished, std::chrono::steady_clock::time_point started,
                         const StatusCallback& statusCallback, bool consoleOutput, bool virtualTerminal,
                         bool paused, unsigned int adaptiveLimit) {
    const uint64_t received = std::min(total, downloaded.load());
    const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    const unsigned int percent = total == 0 ? 0 : static_cast<unsigned int>((static_cast<double>(received) / total) * 100.0);
    const std::string eta = etaText(total, received, elapsed);
    std::ostringstream frame;
    if (virtualTerminal) frame << "\x1b[H\x1b[2J\x1b[36mTermIDM\x1b[0m\n"
                               << (paused ? "Paused — Ctrl+R resumes.\n" : "Downloading — Ctrl+P pauses.\n");
    if (virtualTerminal) frame << "Adaptive connections: " << adaptiveLimit << '/' << workerCount << '\n';
    frame << filename << " downloaded:" << megabytes(received) << ' '
          << progressBar(received, total, 28) << ' ' << std::setw(3) << percent
          << "% ETA:" << eta << " size:" << megabytes(total) << '\n';
    const uint64_t perWorkerTarget = total / workerCount + (total % workerCount != 0);
    for (unsigned int i = 0; i < workerCount; ++i) {
        const int state = progress[i].state.load();
        const uint64_t taskBytes = progress[i].taskBytes.load();
        const uint64_t taskSize = progress[i].taskSize.load();
        uint64_t barBytes = progress[i].bytes.load();
        if (state == 1 || state == 2) { barBytes = taskBytes; }
        if (state == 3 && finished == workerCount) barBytes = perWorkerTarget;
        std::string status;
        if (paused) status = "Paused";
        else if (state == 1) status = "Downloading";
        else if (state == 2) status = "Helping T" + std::to_string(progress[i].homeThread.load() + 1);
        else if (finished == workerCount && state == 3) status = "Done";
        else status = "Waiting";
        frame << 'T' << i + 1 << ' ' << std::setw(8) << megabytes(progress[i].bytes.load()) << ' '
              << progressBar(barBytes, state == 1 || state == 2 ? taskSize : perWorkerTarget, 22)
              << " Status:" << status << '\n';
    }
    if (virtualTerminal) frame << "\nCtrl+P pauses | Ctrl+R resumes | Ctrl+C cancels.\n";
    if (consoleOutput && virtualTerminal) std::cout << frame.str() << std::flush;
    else if (consoleOutput) {
        const unsigned int active = workerCount - std::min(workerCount, finished);
        std::cout << '\r' << progressBar(received, total, 24) << ' ' << std::setw(3) << percent
                  << "%  ETA: " << eta << "  Active threads: " << active
                  << "  Connections: " << adaptiveLimit << '/' << workerCount
                  << (paused ? "  PAUSED (Ctrl+R resumes)    " : "  Ctrl+P pauses    ") << std::flush;
    }
    if (statusCallback) {
        statusCallback(L"Downloading " + std::to_wstring(percent) + L"%  ETA: " + utf8ToWide(eta) +
                       L"  Connections: " + std::to_wstring(adaptiveLimit) + L"/auto");
    }
}

int runDownloader(const std::string& url, const std::wstring& outputPath, const std::string& filename,
                  unsigned int requestedSegments, uint64_t fileSize,
                  const std::string& etag, const std::string& lastModified,
                  const StatusCallback& statusCallback = {},
                  std::atomic_bool* cancel = nullptr, bool terminalUi = false,
                  std::atomic_bool* paused = nullptr, uint64_t* writtenOut = nullptr,
                  const ProgressCallback& progressCallback = {},
                  uint64_t maxReceiveBytesPerSecond = 0, long connectTimeoutSeconds = 90) {
    if (writtenOut) *writtenOut = 0;
    if (fileSize > static_cast<uint64_t>((std::numeric_limits<LONGLONG>::max)())) {
        std::cerr << "Remote file is too large for Windows file offsets.\n";
        return 1;
    }
    constexpr uint64_t kUsefulWorkerRange = 4ULL * 1024 * 1024;
    const uint64_t possibleWorkers = fileSize / kUsefulWorkerRange + (fileSize % kUsefulWorkerRange != 0);
    const unsigned int workerCount = static_cast<unsigned int>(std::max<uint64_t>(1,
        std::min<uint64_t>(requestedSegments, possibleWorkers)));
    if (cancel && cancel->load()) return 1;
    const std::filesystem::path outputFile(outputPath);
    const std::filesystem::path parent = outputFile.parent_path();
    if (!parent.empty()) {
        std::error_code directoryError;
        std::filesystem::create_directories(parent, directoryError);
        if (directoryError || !std::filesystem::is_directory(parent, directoryError)) {
            std::cerr << "Could not create or access output directory '" << wideToUtf8(parent.native()) << "'";
            if (directoryError) std::cerr << " (" << directoryError.message() << ')';
            std::cerr << ".\n";
            return 1;
        }
    }
    const std::wstring partPath = outputPath + L".part";
    const std::wstring ledgerPath = outputPath + L".termidm";
    const std::wstring lockPath = ledgerPath + L".lock";
    DownloadFileLock fileLock(lockPath);
    if (!fileLock.acquired()) {
        std::cerr << "This destination is already being downloaded by another TermIDM process, or its lock cannot be opened.\n";
        return 1;
    }
    DeleteFileW((ledgerPath + L".tmp").c_str());
    std::error_code identityError;
    const auto absoluteOutput = std::filesystem::absolute(outputFile, identityError).lexically_normal();
    if (identityError) {
        std::cerr << "Could not normalize the destination path: " << identityError.message() << '\n';
        return 1;
    }
    const std::string destinationIdentity = wideToUtf8(absoluteOutput.native());
    if (destinationIdentity.empty() || destinationIdentity.size() > kLedgerMaxPathBytes ||
        !validLedgerText(destinationIdentity, kLedgerMaxPathBytes)) {
        std::cerr << "The destination path cannot be represented safely in the recovery ledger.\n";
        return 1;
    }
    const bool resumeSupported = usableResumeValidator(etag, lastModified);
    DownloadLedger ledger;
    bool resuming = false;
    if (resumeSupported) {
        DownloadLedger candidate;
        std::error_code stateError;
        const bool ledgerLoaded = loadLedger(ledgerPath, candidate);
        const bool partExists = std::filesystem::exists(partPath, stateError);
        if (!stateError && ledgerLoaded && partExists &&
            ledgerMatches(candidate, fileSize, destinationIdentity, etag, lastModified)) {
            const uint64_t partSize = std::filesystem::file_size(partPath, stateError);
            if (!stateError && partSize == fileSize) {
                ledger = std::move(candidate);
                resuming = true;
            }
        }
    }
    if (!resuming) {
        std::error_code cleanupError;
        std::filesystem::remove(partPath, cleanupError);
        if (cleanupError) {
            std::cerr << "Could not remove an incompatible partial file: " << cleanupError.message() << '\n';
            return 1;
        }
        cleanupError.clear();
        std::filesystem::remove(ledgerPath, cleanupError);
        if (cleanupError) {
            std::cerr << "Could not remove an incompatible recovery ledger: " << cleanupError.message() << '\n';
            return 1;
        }
        ledger = DownloadLedger{};
        ledger.fileSize = fileSize;
        ledger.destination = destinationIdentity;
        ledger.etag = etag;
        ledger.lastModified = lastModified;
        const uint64_t bitmapLength = ledgerBitmapBytes(ledgerBlockCount(fileSize));
        if (bitmapLength > kMaximumLedgerBytes) {
            std::cerr << "The recovery bitmap would exceed the safe ledger size limit.\n";
            return 1;
        }
        try { ledger.bitmap.assign(static_cast<size_t>(bitmapLength), 0); }
        catch (...) {
            std::cerr << "Could not allocate the recovery bitmap.\n";
            return 1;
        }
    }
    if (!preallocateFile(partPath, fileSize, resuming)) return 1;
    if (resumeSupported && !resuming && !saveLedgerAtomic(ledgerPath, ledger)) {
        DeleteFileW(partPath.c_str());
        std::cerr << "Could not create the recovery ledger; download was not started.\n";
        return 1;
    }
    if (resuming) std::cout << "Resuming from the validated recovery ledger.\n";
    else if (!resumeSupported) std::cout << "This server supplied no reliable ETag or Last-Modified validator; crash recovery is unavailable for this download.\n";
    std::unique_ptr<AsyncDiskWriter> diskWriter;
    try {
        diskWriter = std::make_unique<AsyncDiskWriter>(partPath, workerCount);
    } catch (const std::exception& e) {
        std::cerr << "Could not initialize asynchronous disk writer: " << e.what() << '\n';
        return 1;
    }

    // More, smaller ranges reduce the slow-worker tail and make retries cheaper.
    // Workers still reuse their easy handle/connection and pull tasks dynamically.
    const uint64_t desiredTaskCount = static_cast<uint64_t>(workerCount) * 8;
    uint64_t taskSize = fileSize / desiredTaskCount + (fileSize % desiredTaskCount != 0);
    taskSize = std::clamp<uint64_t>(taskSize, 4ULL * 1024 * 1024, 64ULL * 1024 * 1024);
    taskSize = ((taskSize + kLedgerBlockSize - 1) / kLedgerBlockSize) * kLedgerBlockSize;
    const uint64_t logicalSegmentSize = fileSize / workerCount + (fileSize % workerCount != 0);
    std::vector<std::vector<RangeTask>> tasksByOwner(workerCount);
    const uint64_t blockCount = ledgerBlockCount(fileSize);
    const uint64_t maxTaskBlocks = std::max<uint64_t>(1, taskSize / kLedgerBlockSize);
    uint64_t resumedBytes = 0;
    for (uint64_t block = 0; block < blockCount;) {
        const uint64_t blockStart = block * kLedgerBlockSize;
        const uint64_t blockBytes = std::min<uint64_t>(kLedgerBlockSize, fileSize - blockStart);
        if (ledgerBit(ledger.bitmap, block)) {
            resumedBytes += blockBytes;
            ++block;
            continue;
        }
        const uint64_t firstBlock = block;
        while (block < blockCount && block - firstBlock < maxTaskBlocks && !ledgerBit(ledger.bitmap, block)) ++block;
        const uint64_t first = firstBlock * kLedgerBlockSize;
        const uint64_t exclusiveEnd = std::min<uint64_t>(fileSize, block * kLedgerBlockSize);
        const unsigned int home = static_cast<unsigned int>(std::min<uint64_t>(first / logicalSegmentSize, workerCount - 1));
        tasksByOwner[home].push_back(RangeTask{first, exclusiveEnd - 1, home});
    }
    std::deque<RangeTask> tasks;
    for (size_t block = 0;; ++block) {
        bool added = false;
        for (unsigned int owner = 0; owner < workerCount; ++owner) {
            if (block < tasksByOwner[owner].size()) {
                tasks.push_back(tasksByOwner[owner][block]);
                added = true;
            }
        }
        if (!added) break;
    }

    std::vector<Segment> segments(workerCount);
    std::vector<WorkerProgress> progress(workerCount);
    std::vector<std::thread> workers;
    workers.reserve(workerCount);
    std::mutex queueMutex;
    std::mutex logMutex;
    std::atomic<uint64_t> totalWritten{resumedBytes};
    std::atomic<uint64_t> totalReceived{0};
    std::atomic<unsigned int> finishedWorkers{0};
    std::atomic_bool stop{false};
    std::atomic_bool failed{false};
    AdaptiveController adaptiveController(workerCount);
    CompletedRangeQueue completedRanges;
    std::vector<RangeTask> uncommittedRanges;
    uint64_t uncommittedBlocks = 0;
    auto lastCheckpoint = std::chrono::steady_clock::now();
    bool ledgerCommitFailed = false;
    auto checkpoint = [&](bool force) {
        auto finished = completedRanges.takeAll();
        for (const auto& range : finished) {
            uncommittedRanges.push_back(range);
            uncommittedBlocks += (range.last / kLedgerBlockSize) - (range.first / kLedgerBlockSize) + 1;
        }
        if (!resumeSupported) { uncommittedRanges.clear(); uncommittedBlocks = 0; return true; }
        const auto now = std::chrono::steady_clock::now();
        if (uncommittedRanges.empty() || (!force && uncommittedBlocks < 16 && now - lastCheckpoint < std::chrono::seconds(5))) return true;
        if (!diskWriter->flushData()) return false;
        DownloadLedger updated = ledger;
        for (const auto& range : uncommittedRanges) {
            const uint64_t firstBlock = range.first / kLedgerBlockSize;
            const uint64_t lastBlock = range.last / kLedgerBlockSize;
            for (uint64_t b = firstBlock; b <= lastBlock; ++b) setLedgerBit(updated.bitmap, b);
        }
        if (!saveLedgerAtomic(ledgerPath, updated)) return false;
        ledger = std::move(updated);
        uncommittedRanges.clear();
        uncommittedBlocks = 0;
        lastCheckpoint = now;
        return true;
    };

    bool consoleOutput = false;
    bool virtualTerminal = false;
    if (terminalUi) {
        HANDLE console = GetStdHandle(STD_OUTPUT_HANDLE);
        DWORD mode = 0;
        if (console != INVALID_HANDLE_VALUE && GetConsoleMode(console, &mode)) {
            consoleOutput = true;
            virtualTerminal = SetConsoleMode(console, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING) != FALSE;
            if (virtualTerminal) std::cout << "\x1b[?1049h\x1b[?25l";
        }
    }

    const auto started = std::chrono::steady_clock::now();
    const std::string rangeValidator = (!etag.empty() && etag.rfind("W/", 0) != 0) ? etag : lastModified;
    try {
        for (unsigned int i = 0; i < workerCount; ++i) {
            segments[i].url = &url;
            segments[i].path = &partPath;
            segments[i].ifRangeValidator = rangeValidator.empty() ? nullptr : &rangeValidator;
            segments[i].cancel = cancel;
            segments[i].paused = paused;
            segments[i].stop = &stop;
            segments[i].totalWritten = &totalWritten;
            segments[i].totalReceived = &totalReceived;
            segments[i].progress = &progress[i];
            segments[i].controller = &adaptiveController;
            segments[i].connectTimeoutSeconds = connectTimeoutSeconds;
            segments[i].maxReceiveBytesPerSecond = maxReceiveBytesPerSecond == 0 ? 0 :
                static_cast<curl_off_t>(maxReceiveBytesPerSecond);
            workers.emplace_back(downloadSegment, i, std::ref(tasks), std::ref(queueMutex),
                std::ref(segments[i]), std::ref(progress[i]), std::ref(finishedWorkers),
                std::ref(stop), std::ref(failed), std::ref(logMutex), virtualTerminal,
                std::ref(*diskWriter), std::ref(completedRanges));
        }
    } catch (const std::exception& e) {
        std::cerr << "Could not start all worker threads: " << e.what() << '\n';
        failed.store(true);
        stop.store(true);
    }

    if (!workers.empty()) {
        do {
            if (terminalUi) pollConsolePauseKeys();
            const bool forceCheckpoint = (paused && paused->load()) || (cancel && cancel->load()) ||
                                         finishedWorkers.load() == workers.size();
            if (!ledgerCommitFailed && !checkpoint(forceCheckpoint)) {
                ledgerCommitFailed = true;
                failed.store(true);
                stop.store(true);
                std::cerr << "Recovery checkpoint failed; preserving the last committed ledger and partial file.\n";
            }
            const bool isPaused = paused && paused->load();
            adaptiveController.observe(totalReceived.load(), diskWriter->outstanding(),
                                       std::chrono::steady_clock::now());
            renderDownloadFrame(filename, fileSize, totalWritten, progress, workerCount,
                finishedWorkers.load(), started, statusCallback, consoleOutput, virtualTerminal,
                isPaused, adaptiveController.limit());
            if (progressCallback) progressCallback(totalWritten.load(), fileSize, finishedWorkers.load(),
                workerCount, progress);
            if (finishedWorkers.load() < workers.size()) std::this_thread::sleep_for(std::chrono::milliseconds(250));
        } while (finishedWorkers.load() < workers.size());
    }
    for (auto& worker : workers) if (worker.joinable()) worker.join();
    if (!ledgerCommitFailed && !checkpoint(true)) {
        ledgerCommitFailed = true;
        failed.store(true);
        std::cerr << "Final recovery checkpoint failed; preserving the partial file and prior ledger.\n";
    }
    if (writtenOut) *writtenOut = totalWritten.load();
    if (virtualTerminal) std::cout << "\x1b[?25h\x1b[?1049l" << std::flush;
    if (!diskWriter->flushData()) {
        failed.store(true);
        std::cerr << "Could not flush partial download data to disk.\n";
    }
    diskWriter->close();

    const bool allBytesWritten = totalWritten.load() == fileSize;
    if (cancel && cancel->load() && !allBytesWritten) {
        if (statusCallback) statusCallback(resumeSupported
            ? L"Cancelled. Completed ranges are saved; retry the same URL and location to resume."
            : L"Cancelled. This server cannot be safely resumed; partial data was discarded.");
        if (!resumeSupported) DeleteFileW(partPath.c_str());
        return 1;
    }
    if (failed.load() || !allBytesWritten || finishedWorkers.load() != workerCount) {
        std::cerr << "Download incomplete; " << (resumeSupported
            ? "validated partial data is retained for a retry.\n"
            : "partial data cannot be resumed and will be discarded.\n");
        if (!resumeSupported) DeleteFileW(partPath.c_str());
        if (statusCallback) statusCallback(resumeSupported
            ? L"Download incomplete. Validated ranges are saved; retry the same URL and location to resume."
            : L"Download incomplete. This server cannot be safely resumed; partial data was discarded.");
        return 1;
    }
    bool flushed = true;
    if (terminalUi) {
        std::cout << "Downloaded. Finalizing file...\n" << std::flush;
        std::cout << "====#Done#===\n";
    } else {
        if (!diskWriter->flushData()) flushed = false;
    }
    if (!flushed) {
        std::cerr << "Could not flush completed file data to disk; preserving partial file and recovery ledger.\n";
        return 1;
    }
    bool finalized = false;
    DWORD finalizeError = ERROR_SUCCESS;
    constexpr unsigned int kFinalizeAttempts = 8;
    for (unsigned int attempt = 0; attempt < kFinalizeAttempts; ++attempt) {
        if (MoveFileExW(partPath.c_str(), outputPath.c_str(),
                        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            finalized = true;
            break;
        }
        finalizeError = GetLastError();
        const bool transientLock = finalizeError == ERROR_ACCESS_DENIED ||
            finalizeError == ERROR_SHARING_VIOLATION || finalizeError == ERROR_LOCK_VIOLATION ||
            finalizeError == ERROR_USER_MAPPED_FILE;
        if (!transientLock || attempt + 1 == kFinalizeAttempts) break;
        // Antivirus scanners and indexers can briefly open the completed .part
        // file without delete sharing. Retry those transient locks before
        // surfacing an error; the validated part and ledger remain resumable.
        Sleep(200u * (attempt + 1));
    }
    if (!finalized) {
        std::cerr << "Could not move completed partial file into place (Windows error " << finalizeError
                  << "); the complete .part file and ledger were retained.\n";
        if (statusCallback) statusCallback(L"All bytes are downloaded, but Windows could not finalize the file (error " +
            std::to_wstring(finalizeError) + L"). The complete partial file is retained; close anything using the target and retry.");
        return 1;
    }
    DeleteFileW(ledgerPath.c_str());
    DeleteFileW((ledgerPath + L".tmp").c_str());
    const INT_PTR explorerResult = reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", L"explorer.exe",
        (L"/select,\"" + outputPath + L"\"").c_str(), nullptr, SW_SHOWNORMAL));    if (explorerResult <= 32) std::cerr << "Download finished, but Explorer could not open the file location.\n";
    if (statusCallback) statusCallback(explorerResult > 32
        ? L"Download complete. Opened the file location in Explorer."
        : L"Download complete. Explorer could not open the file location.");
    return 0;
}

struct DownloadRequest {
    std::string url;
    std::wstring destination;
    unsigned int segments = kDefaultSegments;
    bool destinationIsFolder = true;
    uint64_t maxReceiveBytesPerSecond = 0;
    long connectTimeoutSeconds = 90;
};

std::string csvField(const std::string& value) {
    std::string escaped = "\"";
    for (char c : value) {
        if (c == '"') escaped += "\"\"";
        else if (c != '\r') escaped += c;
    }
    escaped += '"';
    return escaped;
}

std::string localTimestamp(std::chrono::system_clock::time_point value) {
    const std::time_t time = std::chrono::system_clock::to_time_t(value);
    std::tm local{};
    if (localtime_s(&local, &time) != 0) return "unknown";
    std::ostringstream formatted;
    formatted << std::put_time(&local, "%Y-%m-%d %H:%M:%S");
    return formatted.str();
}

void appendDownloadHistory(const std::wstring& destination, unsigned int segments,
                           uint64_t expectedBytes, uint64_t writtenBytes, int result,
                           bool cancelled, std::chrono::system_clock::time_point wallStart,
                           std::chrono::system_clock::time_point wallFinish,
                           std::chrono::steady_clock::duration elapsed) {
    std::array<wchar_t, 32768> localAppData{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData.data(),
                                                  static_cast<DWORD>(localAppData.size()));
    if (length == 0 || length >= localAppData.size()) {
        std::cerr << "Download result could not be logged because the history path is unavailable.\n";
        return;
    }

    const std::filesystem::path logDirectory = std::filesystem::path(localAppData.data()) / L"TermIDM";
    const std::filesystem::path logPath = logDirectory / L"download-history.csv";
    std::error_code fsError;
    std::filesystem::create_directories(logDirectory, fsError);
    if (fsError) {
        std::cerr << "Download result could not be logged because its history directory could not be created: "
                  << fsError.message() << '\n';
        return;
    }

    const double elapsedSeconds = std::chrono::duration<double>(elapsed).count();
    const double averageMiBPerSecond = elapsedSeconds > 0.0
        ? (static_cast<double>(writtenBytes) / (1024.0 * 1024.0)) / elapsedSeconds : 0.0;
    const char* outcome = result == 0 ? "completed" : (cancelled ? "cancelled" : "failed");
    std::ostringstream row;
    row << csvField(localTimestamp(wallStart)) << ',' << csvField(localTimestamp(wallFinish)) << ','
        << std::fixed << std::setprecision(3) << elapsedSeconds << ',' << outcome << ','
        << expectedBytes << ',' << writtenBytes << ',' << std::setprecision(2)
        << averageMiBPerSecond << ',' << segments << ',' << csvField(wideToUtf8(destination)) << "\r\n";
    static std::mutex historyMutex;
    std::lock_guard<std::mutex> lock(historyMutex);
    const bool needsHeader = !std::filesystem::exists(logPath, fsError) ||
                             std::filesystem::file_size(logPath, fsError) == 0;
    if (fsError) {
        std::cerr << "Download result could not be logged because the history file could not be inspected: "
                  << fsError.message() << '\n';
        return;
    }
    std::ofstream log(logPath, std::ios::binary | std::ios::app);
    if (!log) {
        std::cerr << "Download result could not be logged because the history file could not be opened.\n";
        return;
    }
    if (needsHeader) log << "started_local,finished_local,elapsed_seconds,result,expected_bytes,written_bytes,average_MiB_per_second,threads,destination\r\n";
    const std::string line = row.str();
    log.write(line.data(), static_cast<std::streamsize>(line.size()));
    if (!log) std::cerr << "Download result could not be logged because the history entry could not be written.\n";
}

int executeRequest(const DownloadRequest& request, const StatusCallback& status = {},
                   std::atomic_bool* cancel = nullptr, bool terminalUi = false,
                   std::atomic_bool* paused = nullptr,
                   const ProgressCallback& progressCallback = {}) {
    const auto steadyStart = std::chrono::steady_clock::now();
    const auto wallStart = std::chrono::system_clock::now();
    auto record = [&](const std::wstring& destination, unsigned int segments,
                      uint64_t expected, uint64_t written, int result) {
        appendDownloadHistory(destination, segments, expected, written, result,
            cancel && cancel->load(), wallStart, std::chrono::system_clock::now(),
            std::chrono::steady_clock::now() - steadyStart);
    };
    CurlGlobal curlGlobal;
    if (curlGlobal.code != CURLE_OK) {
        std::cerr << "libcurl initialization failed: " << curl_easy_strerror(curlGlobal.code) << '\n';
        if (status) status(L"Could not initialize the networking engine.");
        record(request.destination, request.segments, 0, 0, 1);
        return 1;
    }
    if (status) status(L"Checking link and reading file details...");
    uint64_t fileSize = 0;
    std::string filename;
    std::string etag;
    std::string lastModified;
    if (!getRemoteFileInfo(request.url, fileSize, filename, etag, lastModified, cancel, request.connectTimeoutSeconds)) {
        if (status) status(L"Could not read file details. Check the link and try again.");
        record(request.destination, request.segments, 0, 0, 1);
        return 1;
    }
    std::filesystem::path output = request.destination;
    if (request.destinationIsFolder) output /= utf8ToWide(filename);
    if (status) status(L"File name: " + utf8ToWide(filename));
    uint64_t writtenBytes = 0;
    const int result = runDownloader(request.url, output.native(), filename, request.segments,
        fileSize, etag, lastModified, status, cancel, terminalUi, paused, &writtenBytes, progressCallback,
        request.maxReceiveBytesPerSecond, request.connectTimeoutSeconds);
    record(output.native(), request.segments, fileSize, writtenBytes, result);
    return result;
}

void writeEngineLine(const std::string& line) {
    static std::mutex outputMutex;
    std::lock_guard<std::mutex> lock(outputMutex);
    HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!output || output == INVALID_HANDLE_VALUE) return;
    std::string record = line + "\n";
    size_t offset = 0;
    while (offset < record.size()) {
        DWORD written = 0;
        const DWORD chunk = static_cast<DWORD>(std::min<size_t>(record.size() - offset,
            static_cast<size_t>((std::numeric_limits<DWORD>::max)())));
        if (!WriteFile(output, record.data() + offset, chunk, &written, nullptr) || written == 0) return;
        offset += written;
    }
}

int runEngineHost(const std::wstring& url, const std::wstring& destination,
                  const std::wstring& segmentText, const std::wstring& speedText,
                  const std::wstring& timeoutText) {
    DownloadRequest request;
    request.url = wideToUtf8(url);
    request.destination = destination;
    request.destinationIsFolder = true;
    try {
        size_t parsed = 0;
        const unsigned long count = std::stoul(segmentText, &parsed);
        if (parsed != segmentText.size() || count == 0 || count > kMaxSegments) throw std::invalid_argument("segments");
        request.segments = static_cast<unsigned int>(count);
    } catch (...) {
        writeEngineLine("ERROR\tConnection limit must be from 1 to 128.");
        return 2;
    }
    try {
        size_t parsed = 0;
        const unsigned long long speed = std::stoull(speedText, &parsed);
        if (parsed != speedText.size() || speed > static_cast<unsigned long long>((std::numeric_limits<curl_off_t>::max)()))
            throw std::invalid_argument("speed limit");
        request.maxReceiveBytesPerSecond = static_cast<uint64_t>(speed);
    } catch (...) {
        writeEngineLine("ERROR\tSpeed limit must be 0 (unlimited) or a positive byte-per-second value.");
        return 2;
    }
    try {
        size_t parsed = 0;
        const unsigned long timeout = std::stoul(timeoutText, &parsed);
        if (parsed != timeoutText.size() || timeout < 5 || timeout > 600) throw std::invalid_argument("timeout");
        request.connectTimeoutSeconds = static_cast<long>(timeout);
    } catch (...) {
        writeEngineLine("ERROR\tNetwork timeout must be between 5 and 600 seconds.");
        return 2;
    }

    gConsoleCancelled.store(false);
    gDownloadPaused.store(false);
    std::thread controlReader([] {
        HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
        if (!input || input == INVALID_HANDLE_VALUE) return;
        std::string pending;
        char buffer[128];
        for (;;) {
            DWORD received = 0;
            if (!ReadFile(input, buffer, sizeof(buffer), &received, nullptr) || received == 0) break;
            for (DWORD i = 0; i < received; ++i) {
                if (buffer[i] == '\n') {
                    const std::string command = lower(trim(pending));
                    if (command == "pause") gDownloadPaused.store(true);
                    else if (command == "resume") gDownloadPaused.store(false);
                    else if (command == "cancel") { gConsoleCancelled.store(true); gDownloadPaused.store(false); }
                    pending.clear();
                } else if (buffer[i] != '\r' && pending.size() < 128) pending.push_back(buffer[i]);
            }
        }
    });

    CurlGlobal curlGlobal;
    if (curlGlobal.code != CURLE_OK) {
        writeEngineLine(std::string("ERROR\tlibcurl initialization failed: ") + curl_easy_strerror(curlGlobal.code));
        CancelSynchronousIo(controlReader.native_handle());
        controlReader.join();
        return 1;
    }
    const StatusCallback status = [](const std::wstring& message) {
        writeEngineLine("STATUS\t" + wideToUtf8(message));
    };
    const ProgressCallback progress = [](uint64_t written, uint64_t total, unsigned int finished,
                                         unsigned int workers, const std::vector<WorkerProgress>& items) {
        std::ostringstream line;
        line << "PROGRESS\t" << written << '\t' << total << '\t' << finished << '\t' << workers;
        for (const auto& item : items)
            line << '\t' << item.state.load() << ',' << item.bytes.load() << ',' << item.taskBytes.load()
                 << ',' << item.taskSize.load() << ',' << item.homeThread.load();
        writeEngineLine(line.str());
    };
    const int result = executeRequest(request, status, &gConsoleCancelled, false, &gDownloadPaused, progress);
    writeEngineLine(result == 0 ? "DONE" : (gConsoleCancelled.load() ? "CANCELLED" : "FAILED"));
    CancelSynchronousIo(controlReader.native_handle());
    controlReader.join();
    return result;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0;
    LPWSTR* wideArgv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!wideArgv) return 1;
    int result = 0;
    if (argc == 7 && std::wstring(wideArgv[1]) == L"--engine") {
        result = runEngineHost(wideArgv[2], wideArgv[3], wideArgv[4], wideArgv[5], wideArgv[6]);
        LocalFree(wideArgv);
        return result;
    }
    MessageBoxW(nullptr, L"TermIDM.Engine is an internal desktop worker. Launch TermIDM.exe to use the application.",
        L"TermIDM", MB_OK | MB_ICONINFORMATION);
    LocalFree(wideArgv);
    return 2;
}
