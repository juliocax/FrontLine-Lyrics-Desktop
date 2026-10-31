"""Service status inferred ONLY from actual user-triggered requests. No probes."""
import threading
import logging
import os
import requests

_lock = threading.Lock()
_status = "ok"
_fail_kind = None
_fail_count = 0
FAILURE_ATTEMPTS = 3
DEBUG = os.getenv("FRONTLINE_SERVICE_DEBUG", "0").lower() in ("1", "true", "yes", "on")

def debug(message, *args):
    if DEBUG:
        logging.warning("[SERVICE DEBUG] " + message, *args)

def begin_user_attempt():
    """Temporarily hide an old banner while allowing a new user-initiated attempt."""
    global _status
    with _lock:
        _status = "ok"
    debug("new user action; previous failure counter preserved for next result")



def status():
    with _lock:
        return _status


def record_attempt(kind, *, immediate=False):
    """One completed LRCLIB song lookup, not each HTTP call/retry."""
    global _status, _fail_kind, _fail_count
    with _lock:
        if kind == "ok":
            _status, _fail_kind, _fail_count = "ok", None, 0
        elif kind in ("service_offline", "connection_error"):
            if kind != _fail_kind:
                _fail_kind, _fail_count = kind, 0
            _fail_count += 1
            if immediate or _fail_count >= FAILURE_ATTEMPTS:
                _status = kind
        result = _status
    debug("attempt=%s count=%d immediate=%s status=%s", kind, _fail_count, immediate, result)
    return result


def classify_exception(exc):
    """Classify failure using actual request errors, not the Windows network icon."""
    if isinstance(exc, requests.exceptions.ConnectionError):
        return "connection_error"
    if isinstance(exc, requests.exceptions.Timeout):
        return "connection_error"
    return None


def connectivity_after_failed_recognition():
    """Event-driven connectivity diagnosis after an unsuccessful Shazam attempt.

    Never polls while idle, and never touches LRCLIB. A successful HTTPS response
    (including HTTP errors) proves a network path exists. Both independent
    connectivity targets must fail to mark the user as disconnected.
    """
    import urllib.request
    import urllib.error
    for url in ("https://www.msftconnecttest.com/connecttest.txt", "https://www.google.com/generate_204"):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, method="GET"), timeout=3) as response:
                debug("connectivity reachable url=%s status=%s", url, response.status)
                return True
        except urllib.error.HTTPError as exc:
            debug("connectivity reachable url=%s HTTP=%d", url, exc.code)
            return True
        except (OSError, urllib.error.URLError) as exc:
            debug("connectivity failed url=%s reason=%s", url, type(exc).__name__)
    record_attempt("connection_error", immediate=True)
    return False
