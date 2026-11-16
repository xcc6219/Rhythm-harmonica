"""Trim the installed Basic Pitch inference runtime without changing its model.

Only the adjacent application's runtime/python directory is accepted. Every path
is validated before mutation; links and Windows junctions are rejected. Copyright
and license records are retained even for packages not used during inference.
"""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import stat
import subprocess
import sys


UNUSED_PACKAGES = {
    "pip", "pip-25.0.1.dist-info", "wheel", "wheel-0.45.1.dist-info",
    "sympy", "sympy-1.14.0.dist-info", "mpmath", "mpmath-1.3.0.dist-info",
    "sklearn", "scikit_learn-1.7.2.dist-info", "bin", "share",
}
WINDOWS_API = None


def get_windows_api():
    """Keep Windows API access separate from the optional optimization policy."""
    global WINDOWS_API
    if os.name != "nt":
        raise OSError("当前系统不支持 Windows NTFS 压缩")
    if WINDOWS_API is None:
        api = ctypes.WinDLL("kernel32", use_last_error=True)
        api.GetSystemDirectoryW.argtypes = (wintypes.LPWSTR, wintypes.UINT)
        api.GetSystemDirectoryW.restype = wintypes.UINT
        api.GetVolumePathNameW.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
        api.GetVolumePathNameW.restype = wintypes.BOOL
        api.GetVolumeInformationW.argtypes = (
            wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD,
            ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(wintypes.DWORD),
            ctypes.POINTER(wintypes.DWORD), wintypes.LPWSTR, wintypes.DWORD)
        api.GetVolumeInformationW.restype = wintypes.BOOL
        api.GetCompressedFileSizeW.argtypes = (wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD))
        api.GetCompressedFileSizeW.restype = wintypes.DWORD
        WINDOWS_API = api
    return WINDOWS_API


def get_volume_filesystem(runtime):
    api = get_windows_api()
    volume = ctypes.create_unicode_buffer(32768)
    if not api.GetVolumePathNameW(str(runtime), volume, len(volume)):
        raise OSError("无法读取音频组件所在卷")
    filesystem = ctypes.create_unicode_buffer(256)
    if not api.GetVolumeInformationW(volume.value, None, 0, None, None, None,
                                     filesystem, len(filesystem)):
        raise OSError("无法读取音频组件卷格式")
    return filesystem.value


def get_system_compact_path():
    api = get_windows_api()
    system_directory = ctypes.create_unicode_buffer(32768)
    length = api.GetSystemDirectoryW(system_directory, len(system_directory))
    if not length or length >= len(system_directory):
        raise OSError("无法读取 Windows 系统目录")
    directory = Path(system_directory.value)
    if not directory.is_absolute() or directory.name.casefold() != "system32":
        raise OSError("Windows 系统目录不是有效的 System32")
    executable = directory / "compact.exe"
    if not executable.is_file() or is_link(executable):
        raise OSError("Windows 系统压缩工具不可用")
    return executable


def get_compressed_file_size(path):
    """Return the Windows storage size, or None; never substitute logical bytes."""
    try:
        api = get_windows_api()
        high = wintypes.DWORD()
        ctypes.set_last_error(0)
        low = api.GetCompressedFileSizeW(str(path), ctypes.byref(high))
        if low == 0xFFFFFFFF and ctypes.get_last_error() != 0:
            return None
        return (high.value << 32) | low
    except Exception:
        return None


def measure_disk_size(files):
    total = 0
    for path in files:
        try:
            size = get_compressed_file_size(path)
        except Exception:
            return None
        if size is None:
            return None
        total += size
    return total


def run_compact_process(executable, runtime):
    # The caller validates this complete tree immediately before invoking us.
    # A fixed System32 executable and argument list avoid shells and PATH lookup.
    return subprocess.run(
        [str(executable), "/C", "/S:" + str(runtime), "/I", "/A", "/Q", "*"],
        cwd=str(runtime), shell=False, creationflags=subprocess.CREATE_NO_WINDOW,
        capture_output=True, check=False, timeout=300)


def compression_warning(reason):
    return "音频组件空间优化已跳过：" + reason + "；模型仍可使用。"


def compress_ntfs(runtime):
    """Optional compression failures do not undo successful runtime trimming."""
    try:
        filesystem = get_volume_filesystem(runtime)
    except Exception:
        return "skipped", compression_warning("无法确认音频组件所在卷")
    if not isinstance(filesystem, str) or filesystem.casefold() != "ntfs":
        return "skipped", compression_warning("音频组件不在 NTFS 卷")
    try:
        executable = get_system_compact_path()
    except Exception:
        return "skipped", compression_warning("Windows 系统压缩工具不可用")
    try:
        process = run_compact_process(executable, runtime)
        if process.returncode != 0:
            return "skipped", compression_warning("Windows 未完成普通 NTFS 压缩")
    except Exception:
        return "skipped", compression_warning("Windows 系统压缩工具无法完成操作")
    return "compressed", None


def size_mb(size):
    return None if size is None else round(size / 1048576, 2)


def space_report(result):
    logical = "逻辑体积 {:.2f} → {:.2f} MiB".format(
        result["logical_before_mb"], result["logical_after_mb"])
    if result["disk_before_mb"] is None or result["disk_after_mb"] is None:
        disk = "磁盘占用无法测量"
    else:
        disk = "磁盘占用 {:.2f} → {:.2f} MiB".format(
            result["disk_before_mb"], result["disk_after_mb"])
    report = "音频组件空间：" + logical + "；" + disk + "。"
    if result["warning"]:
        report += " " + result["warning"]
    return report


def is_link(path):
    status = path.lstat()
    return stat.S_ISLNK(status.st_mode) or bool(
        getattr(status, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def checked_path(path, runtime):
    if is_link(path):
        raise ValueError("音频组件含符号链接或目录联接，停止精简：" + str(path))
    resolved = path.resolve(strict=True)
    if resolved != runtime and runtime not in resolved.parents:
        raise ValueError("精简路径越过音频组件目录，停止操作：" + str(path))
    return resolved


def can_remove(relative):
    parts = relative.parts
    name = relative.name.lower()
    # Preserve license/copyright/notice files and complete license directories.
    if any(any(token in part.lower() for token in ("license", "copying", "notice", "copyright"))
           for part in parts):
        return False
    if any(part in ("__pycache__", "tests", "test") for part in parts):
        return True
    if len(parts) > 2 and parts[:2] == ("Lib", "site-packages"):
        package = parts[2]
        if package in UNUSED_PACKAGES:
            return True
        if package == "pretty_midi" and name == "timgm6mb.sf2":
            return True
        if package == "basic_pitch" and "saved_models" in parts and name != "nmp.onnx":
            return True
    return False


def compact(directory):
    application = Path(__file__).absolute().parent.parent
    expected = application / "runtime" / "python"
    supplied = Path(os.path.abspath(directory))
    if os.path.normcase(str(supplied)) != os.path.normcase(str(expected)):
        raise ValueError("只能精简本软件目录内的 runtime/python 组件")
    for parent in (application, application / "runtime", expected):
        if is_link(parent):
            raise ValueError("音频组件路径含符号链接或目录联接，停止精简")
    runtime = expected.resolve(strict=True)
    # Embedded Python can install files beyond the legacy Windows path limit.
    # Keep the checked boundary and every inventory path in the same namespace.
    if os.name == "nt" and not str(runtime).startswith("\\\\?\\"):
        runtime = Path("\\\\?\\" + str(runtime))
    if not (runtime / "python.exe").is_file() or not (runtime / "python310._pth").is_file():
        raise ValueError("该目录不是本软件的独立 Python 音频组件")
    # Inventory the entire tree before deleting anything, including link entries.
    paths = list(runtime.rglob("*"))
    for path in paths:
        checked_path(path, runtime)
    files = [path for path in paths if path.is_file()]
    before = sum(path.stat().st_size for path in files)
    disk_before = measure_disk_size(files)
    removed = 0
    removed_count = 0
    for path in files:
        if can_remove(path.relative_to(runtime)):
            checked_path(path, runtime)
            removed += path.stat().st_size
            path.unlink()
            removed_count += 1
    # Remove empty directories only. No recursive deletion is used.
    for path in sorted((path for path in paths if path.is_dir()), key=lambda item: len(item.parts), reverse=True):
        checked_path(path, runtime)
        if not any(path.iterdir()):
            path.rmdir()
    # Recheck the same boundary and every surviving entry before recursive compact.
    # Invalid paths/links remain strict errors, outside the optional-failure handler.
    for parent in (application, application / "runtime", expected):
        if is_link(parent):
            raise ValueError("音频组件路径含符号链接或目录联接，停止精简")
    checked_path(runtime, runtime)
    remaining_paths = list(runtime.rglob("*"))
    for path in remaining_paths:
        checked_path(path, runtime)
    remaining_files = [path for path in remaining_paths if path.is_file()]
    after = sum(path.stat().st_size for path in remaining_files)
    compression_status, warning = compress_ntfs(runtime)
    disk_after = measure_disk_size(remaining_files)
    result = {"before_mb": size_mb(before), "after_mb": size_mb(after),
              "logical_before_mb": size_mb(before), "logical_after_mb": size_mb(after),
              "disk_before_mb": size_mb(disk_before), "disk_after_mb": size_mb(disk_after),
              "removed_mb": size_mb(removed), "removed_files": removed_count,
              "compression_status": compression_status, "warning": warning}
    print(json.dumps(result, ensure_ascii=False), flush=True)
    print(space_report(result), flush=True)
    return result


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    try:
        if len(sys.argv) != 2:
            raise ValueError("用法：compact-audio-runtime.py <runtimeDirectory>")
        compact(sys.argv[1])
    except Exception as error:
        print("音频组件精简失败：" + str(error), file=sys.stderr, flush=True)
        sys.exit(1)
