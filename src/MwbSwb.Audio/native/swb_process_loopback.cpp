#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmreg.h>

class Handler final : public IActivateAudioInterfaceCompletionHandler, public IAgileObject {
  LONG _ref{1};
  HANDLE _done;
  HRESULT _hr{E_FAIL};
  IAudioClient* _client{nullptr};
public:
  explicit Handler(HANDLE done) : _done(done) {}
  IAudioClient* Client() const { return _client; }
  HRESULT Result() const { return _hr; }
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IActivateAudioInterfaceCompletionHandler)) {
      *ppv = static_cast<IActivateAudioInterfaceCompletionHandler*>(this); AddRef(); return S_OK;
    }
    if (riid == __uuidof(IAgileObject)) {
      *ppv = static_cast<IAgileObject*>(this); AddRef(); return S_OK;
    }
    return E_NOINTERFACE;
  }
  ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&_ref); }
  ULONG STDMETHODCALLTYPE Release() override {
    LONG n = InterlockedDecrement(&_ref);
    if (n == 0) delete this;
    return n;
  }
  HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* op) override {
    HRESULT hrActivate = E_FAIL;
    IUnknown* punk = nullptr;
    HRESULT hr = op->GetActivateResult(&hrActivate, &punk);
    _hr = FAILED(hr) ? hr : hrActivate;
    if (SUCCEEDED(hr) && SUCCEEDED(hrActivate) && punk) {
      _hr = punk->QueryInterface(__uuidof(IAudioClient), (void**)&_client);
      punk->Release();
    }
    SetEvent(_done);
    return S_OK;
  }
};

static HRESULT StartProcessLoopback(
    DWORD targetPid,
    BOOL includeTarget,
    HANDLE eventHandle,
    IAudioClient** outClient,
    IAudioCaptureClient** outCapture,
    WAVEFORMATEX* outFormat)
{
  if (!outClient || !outCapture || !eventHandle) return E_POINTER;
  *outClient = nullptr;
  *outCapture = nullptr;

  HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  (void)hrCo;

  HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
  if (!done) return HRESULT_FROM_WIN32(GetLastError());

  Handler* handler = new Handler(done);

  AUDIOCLIENT_ACTIVATION_PARAMS ap = {};
  ap.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
  ap.ProcessLoopbackParams.TargetProcessId = targetPid ? targetPid : GetCurrentProcessId();
  ap.ProcessLoopbackParams.ProcessLoopbackMode = includeTarget
      ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
      : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;

  PROPVARIANT pv = {};
  pv.vt = VT_BLOB;
  pv.blob.cbSize = sizeof(ap);
  pv.blob.pBlobData = (BYTE*)&ap;

  IActivateAudioInterfaceAsyncOperation* asyncOp = nullptr;
  HRESULT hr = ActivateAudioInterfaceAsync(
      VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
      __uuidof(IAudioClient),
      &pv,
      handler,
      &asyncOp);

  IAudioClient* client = nullptr;
  if (SUCCEEDED(hr)) {
    DWORD w = WaitForSingleObject(done, 15000);
    if (w != WAIT_OBJECT_0) hr = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    else hr = handler->Result();
    if (SUCCEEDED(hr)) client = handler->Client();
    if (asyncOp) asyncOp->Release();
  }
  handler->Release();
  CloseHandle(done);
  if (FAILED(hr) || !client) return FAILED(hr) ? hr : E_FAIL;

  WAVEFORMATEX fmt = {};
  fmt.wFormatTag = WAVE_FORMAT_PCM;
  fmt.nChannels = 2;
  fmt.nSamplesPerSec = 48000;
  fmt.wBitsPerSample = 16;
  fmt.nBlockAlign = fmt.nChannels * fmt.wBitsPerSample / 8;
  fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
  fmt.cbSize = 0;

  hr = client->Initialize(
      AUDCLNT_SHAREMODE_SHARED,
      AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
      0, 0, &fmt, nullptr);
  if (FAILED(hr)) {
    hr = client->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
        0, 0, &fmt, nullptr);
  }
  if (FAILED(hr)) { client->Release(); return hr; }

  IAudioCaptureClient* capture = nullptr;
  hr = client->GetService(__uuidof(IAudioCaptureClient), (void**)&capture);
  if (FAILED(hr)) { client->Release(); return hr; }

  hr = client->SetEventHandle(eventHandle);
  if (FAILED(hr)) { capture->Release(); client->Release(); return hr; }

  hr = client->Start();
  if (FAILED(hr)) { capture->Release(); client->Release(); return hr; }

  *outClient = client;
  *outCapture = capture;
  if (outFormat) *outFormat = fmt;
  return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall SwbStartExcludeLoopback(
    DWORD targetPid, HANDLE eventHandle,
    IAudioClient** outClient, IAudioCaptureClient** outCapture, WAVEFORMATEX* outFormat)
{
  return StartProcessLoopback(targetPid, FALSE, eventHandle, outClient, outCapture, outFormat);
}

extern "C" __declspec(dllexport) HRESULT __stdcall SwbStartIncludeLoopback(
    DWORD targetPid, HANDLE eventHandle,
    IAudioClient** outClient, IAudioCaptureClient** outCapture, WAVEFORMATEX* outFormat)
{
  return StartProcessLoopback(targetPid, TRUE, eventHandle, outClient, outCapture, outFormat);
}

extern "C" __declspec(dllexport) HRESULT __stdcall SwbActivateExcludeLoopback(
    DWORD targetPid, IAudioClient** outClient)
{
  if (!outClient) return E_POINTER;
  *outClient = nullptr;
  HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
  if (!done) return HRESULT_FROM_WIN32(GetLastError());
  CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  Handler* handler = new Handler(done);
  AUDIOCLIENT_ACTIVATION_PARAMS ap = {};
  ap.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
  ap.ProcessLoopbackParams.TargetProcessId = targetPid ? targetPid : GetCurrentProcessId();
  ap.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;
  PROPVARIANT pv = {};
  pv.vt = VT_BLOB;
  pv.blob.cbSize = sizeof(ap);
  pv.blob.pBlobData = (BYTE*)&ap;
  IActivateAudioInterfaceAsyncOperation* asyncOp = nullptr;
  HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &pv, handler, &asyncOp);
  if (SUCCEEDED(hr)) {
    WaitForSingleObject(done, 15000);
    hr = handler->Result();
    if (SUCCEEDED(hr)) *outClient = handler->Client();
    if (asyncOp) asyncOp->Release();
  }
  handler->Release();
  CloseHandle(done);
  return hr;
}