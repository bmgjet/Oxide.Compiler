Ai stripped everything not needed and created a http service.<br><br>
Below is complete php for the compiler page on OnlyPlugins.<br>
For reference the website and compiler are running on a old Android phone.<br>
Specifically a OnePlus 9 Pro.<br>
<br>
Arm64<br>
Termux -> Proot<br>
NGINX<br>
PHP-FPM<br>
Dotnet 10<br>
<br>
```php
<?php
declare(strict_types=1);
error_reporting(0);
ini_set('display_errors', '0');
ini_set('display_startup_errors', '0');

// ================= Configuration =================
const COMPILER_HOST    = '127.0.0.1';   // Oxide.Compiler --http host
const COMPILER_PORT    = 5086;             // must match OXIDE_COMPILER_PORT in index.php
const COMPILER_API_KEY = 'change_me';        // must match Http:ApiKey on the compiler
const MAX_UPLOAD_BYTES = 5 * 1024 * 1024;   // 5 MB per file
const REQUEST_TIMEOUT  = 65;                // seconds, a little above the compiler's own TimeoutSeconds
const MAX_BULK_FILES   = 1000;              // app-side cap on bulk batch size
const BULK_CONCURRENCY = 6;                 // parallel requests in bulk mode - matches the compiler's 8 threads per branch
const SELF_FILENAME    = 'compiler.php';    // used to tell "opened directly" apart from "included by index.php"
// ===================================================

const COMPILER_GATE_PASSWORD = 'change_me'; // temporary password — change me

if (session_status() !== PHP_SESSION_ACTIVE) {
    session_name('op_sid');
    session_start();
}

$compilerAccessedDirectly = basename((string) ($_SERVER['SCRIPT_NAME'] ?? '')) === SELF_FILENAME;
$compilerBase = $compilerAccessedDirectly ? '?' : '?page=compiler&';
if ($compilerAccessedDirectly && empty($_SESSION['compiler_gate_ok'])) {
    $gateError = '';
    if (($_SERVER['REQUEST_METHOD'] ?? '') === 'POST' && isset($_POST['compiler_gate_password'])) {
        if (hash_equals(COMPILER_GATE_PASSWORD, (string) $_POST['compiler_gate_password'])) {
            $_SESSION['compiler_gate_ok'] = true;
            header('Location: ' . (string) ($_SERVER['REQUEST_URI'] ?? SELF_FILENAME));
            exit;
        }
        $gateError = 'Incorrect password.';
    }
    ?><!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Compiler — password required</title>
<style>
    body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
        background:#0f1115; color:#e6e8ec; font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; }
    .gate-box { width:100%; max-width:340px; padding:28px; background:#171a21; border:1px solid #2a2f3a; border-radius:12px; }
    .gate-box h1 { font-size:18px; margin:0 0 6px; }
    .gate-box p { color:#8b93a1; font-size:14px; margin:0 0 18px; line-height:1.5; }
    .gate-box input[type=password] {
        width:100%; box-sizing:border-box; padding:10px 12px; border-radius:8px;
        border:1px solid #2a2f3a; background:#0f1115; color:#e6e8ec; font:inherit; margin-bottom:12px;
    }
    .gate-box button {
        width:100%; padding:10px 12px; border-radius:8px; border:none;
        background:#5b8cff; color:#fff; font:inherit; font-weight:600; cursor:pointer;
    }
    .gate-box .err { color:#ff5d5d; font-size:13px; margin:-6px 0 12px; }
</style>
</head>
<body>
    <div class="gate-box">
        <h1>🔒 Compiler</h1>
        <p>This tool was opened directly instead of through the site, so it needs a password.</p>
        <?php if ($gateError !== ''): ?><p class="err"><?= htmlspecialchars($gateError, ENT_QUOTES) ?></p><?php endif; ?>
        <form method="post">
            <input type="password" name="compiler_gate_password" placeholder="Password" autofocus required>
            <button type="submit">Unlock</button>
        </form>
    </div>
</body>
</html>
    <?php
    exit;
}

if ($_SERVER['REQUEST_METHOD'] === 'POST' && ($_POST['action'] ?? '') === 'validate_bulk_item') {
    @set_time_limit(0);
    if (session_status() === PHP_SESSION_ACTIVE) {
        session_write_close();
    }
    header('Content-Type: application/json');

    $itemBranch = normalizeBranch($_POST['branch'] ?? null);
    $itemName   = safeFileName((string)($_POST['name'] ?? 'Plugin.cs'));

    if (!isset($_FILES['file'])) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'No file received.']);
        exit;
    }

    $code = (int)$_FILES['file']['error'];
    $size = (int)($_FILES['file']['size'] ?? 0);

    if ($code !== UPLOAD_ERR_OK) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'Upload failed: ' . uploadErrorMessage($code) . '.']);
        exit;
    }
    if (!is_uploaded_file($_FILES['file']['tmp_name'])) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'Not a valid uploaded file.']);
        exit;
    }
    if ($size === 0) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'File is empty.']);
        exit;
    }
    if ($size > MAX_UPLOAD_BYTES) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'File is larger than ' . (MAX_UPLOAD_BYTES / 1024 / 1024) . ' MB.']);
        exit;
    }

    $resp = submitToCompiler($_FILES['file']['tmp_name'], $itemName, $itemBranch);

    if (!$resp['ok']) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => $resp['error']]);
        exit;
    }
    if ($resp['http'] === 401) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => '401 Unauthorized (check COMPILER_API_KEY).']);
        exit;
    }
    if ($resp['http'] !== 200 && $resp['http'] !== 504) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'Compiler returned unexpected HTTP ' . $resp['http'] . '.']);
        exit;
    }

    $decoded = json_decode((string)$resp['body'], true);
    if (!is_array($decoded)) {
        echo json_encode(['status' => 'error', 'name' => $itemName, 'error' => 'Compiler response was not valid JSON.']);
        exit;
    }

    echo json_encode([
        'status' => !empty($decoded['success']) ? 'pass' : 'fail',
        'name'   => $itemName,
        'result' => $decoded,
    ]);
    exit;
}

function submitToCompiler(string $localPath, string $fileName, string $branch): array
{
    $url = sprintf('http://%s:%d/validate', COMPILER_HOST, COMPILER_PORT);

    $headers = ['Expect:'];
    if (COMPILER_API_KEY !== '') {
        $headers[] = 'X-Api-Key: ' . COMPILER_API_KEY;
    }

    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_POST            => true,
        CURLOPT_RETURNTRANSFER  => true,
        CURLOPT_CONNECTTIMEOUT  => 10, 
        CURLOPT_TIMEOUT         => REQUEST_TIMEOUT,
        CURLOPT_HTTPHEADER      => $headers,
        CURLOPT_POSTFIELDS      => [
            'file'            => new CURLFile($localPath, 'text/plain', $fileName),
            'languageVersion' => 'Latest',
            'branch'          => $branch,
        ],
    ]);

    $body   = curl_exec($ch);
    $errno  = curl_errno($ch);
    $error  = curl_error($ch);
    $status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);

    if ($errno !== 0 || $body === false) {
        return ['ok' => false, 'error' => $error !== '' ? $error : 'Could not reach the compiler service.'];
    }

    return ['ok' => true, 'http' => $status, 'body' => (string)$body];
}

function normalizeBranch(?string $value): string
{
    $value = strtolower(trim((string)$value));
    return $value === 'staging' ? 'staging' : 'main';
}

function safeFileName(string $name): string
{
    $name = basename(trim($name));
    if ($name === '' || !str_ends_with(strtolower($name), '.cs')) {
        $name = 'Plugin.cs';
    }
    $name = preg_replace('/[^A-Za-z0-9._-]/', '_', $name) ?? 'Plugin.cs';
    return $name;
}

function parseIniSize(string $value): int
{
    $value = trim($value);
    if ($value === '') {
        return 0;
    }
    $unit = strtolower(substr($value, -1));
    $num  = (int)$value;
    return match ($unit) {
        'g' => $num * 1024 * 1024 * 1024,
        'm' => $num * 1024 * 1024,
        'k' => $num * 1024,
        default => $num,
    };
}

function uploadErrorMessage(int $code): string
{
    return match ($code) {
        UPLOAD_ERR_INI_SIZE   => 'file exceeds upload_max_filesize',
        UPLOAD_ERR_FORM_SIZE  => 'file exceeds MAX_FILE_SIZE',
        UPLOAD_ERR_PARTIAL    => 'file only partially uploaded',
        UPLOAD_ERR_NO_FILE    => 'no file uploaded',
        UPLOAD_ERR_NO_TMP_DIR => 'missing temp folder',
        UPLOAD_ERR_CANT_WRITE => 'failed to write to disk',
        UPLOAD_ERR_EXTENSION  => 'upload blocked by extension',
        default               => 'unknown error',
    };
}

$mode   = $_GET['mode'] ?? $_POST['mode'] ?? 'single';
$mode   = in_array($mode, ['single', 'bulk'], true) ? $mode : 'single';
$branch = normalizeBranch($_POST['branch'] ?? $_GET['branch'] ?? null);

$result       = null;
$connectError = null;
$source       = '';
$fileName     = 'Plugin.cs';

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $action = (string)($_POST['action'] ?? '');

    if ($action === 'validate' && $mode === 'single') {
        $source   = (string)($_POST['source'] ?? '');
        $fileName = safeFileName((string)($_POST['fileName'] ?? 'Plugin.cs'));

        if (trim($source) === '') {
            $connectError = 'Nothing to validate - the editor is empty.';
        } elseif (strlen($source) > MAX_UPLOAD_BYTES) {
            $connectError = 'Source is larger than ' . (MAX_UPLOAD_BYTES / 1024 / 1024) . ' MB.';
        } else {
            $tmpPath = tempnam(sys_get_temp_dir(), 'oxvalidate_');
            if ($tmpPath === false) {
                $connectError = 'Could not create a temporary file on the web server.';
            } else {
                file_put_contents($tmpPath, $source);

                @set_time_limit(0); 
                if (session_status() === PHP_SESSION_ACTIVE) {
                    session_write_close();
                }
                $response = submitToCompiler($tmpPath, $fileName, $branch);
                unlink($tmpPath);

                if (!$response['ok']) {
                    $connectError = $response['error'];
                } elseif ($response['http'] === 401) {
                    $connectError = 'Rejected: 401 Unauthorized. Check COMPILER_API_KEY matches the server\'s Http:ApiKey.';
                } elseif ($response['http'] !== 200 && $response['http'] !== 504) {
                    $connectError = 'Compiler returned unexpected HTTP ' . $response['http'] . '.';
                } else {
                    $decoded = json_decode($response['body'], true);
                    if (!is_array($decoded)) {
                        $connectError = 'Compiler response was not valid JSON.';
                    } else {
                        $result = $decoded;
                    }
                }
            }
        }
    }
}

$errorsByLine = [];
if ($result !== null && !empty($result['errors'])) {
    foreach ($result['errors'] as $err) {
        $line = (int)($err['line'] ?? 0);
        $errorsByLine[$line][] = $err;
    }
}
$sourceLines = $source !== '' ? explode("\n", str_replace("\r\n", "\n", $source)) : [];
?>
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Plugin Validator</title>
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.css">
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/theme/material-darker.min.css">
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/dialog/dialog.min.css">
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/mode/clike/clike.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/edit/matchbrackets.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/edit/closebrackets.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/selection/active-line.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/search/searchcursor.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/search/search.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/dialog/dialog.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/fold/foldcode.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/fold/foldgutter.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/fold/brace-fold.min.js"></script>
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/addon/fold/foldgutter.min.css">
<style>
  :root {
    --bg: #0f1115; --panel: #171a21; --border: #2a2f3a;
    --text: #e6e8ec; --muted: #8b93a1;
    --ok: #33c481; --ok-bg: #12281f;
    --bad: #ff5d5d; --bad-bg: #2a1417;
    --warn: #f0b95a; --warn-bg: #2a2117;
    --accent: #5b8cff;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 30px 20px 80px;
    background: var(--bg); color: var(--text);
    font-family: -apple-system, Segoe UI, Roboto, Helvetica, Arial, sans-serif;
  }
  .wrap { max-width: 1100px; margin: 0 auto; }
  .back-link {
    display: inline-flex; align-items: center; gap: 6px;
    color: var(--muted); text-decoration: none; font-size: 13px;
    margin-bottom: 12px;
  }
  .back-link:hover { color: var(--text); }
  h1 { font-size: 22px; margin-bottom: 4px; }
  p.sub { color: var(--muted); margin-top: 0; }

  .tabs {
    display: inline-flex; gap: 4px; margin: 18px 0 14px;
    background: var(--panel); border: 1px solid var(--border);
    border-radius: 10px; padding: 4px;
  }
  .tab {
    padding: 8px 16px; border-radius: 6px; color: var(--muted);
    text-decoration: none; font-size: 14px; font-weight: 500;
  }
  .tab:hover { color: var(--text); }
  .tab.active { background: var(--accent); color: #fff; }
  .tab.active:hover { color: #fff; }

  .toolbar {
    background: var(--panel); border: 1px solid var(--border);
    border-radius: 10px; padding: 16px 18px; display: flex; gap: 18px;
    align-items: center; flex-wrap: wrap;
  }
  .branch-group { display: flex; gap: 14px; align-items: center; font-size: 14px; }
  .branch-group label { display: flex; align-items: center; gap: 6px; cursor: pointer; }
  .branch-group input[type=radio] { accent-color: var(--accent); width: 16px; height: 16px; }
  .filename-field { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--muted); }
  .filename-field input {
    background: #0d0f13; border: 1px solid var(--border); color: var(--text);
    padding: 6px 10px; border-radius: 6px; font-family: Consolas, Menlo, monospace; font-size: 13px;
    width: 220px;
  }
  .hook-picker { display: inline-flex; gap: 6px; align-items: center; }
  .hook-search {
    background: #0d0f13; border: 1px solid var(--border); color: var(--text);
    padding: 8px 10px; border-radius: 6px; font-size: 13px; width: 130px;
  }
  .hook-search::placeholder { color: var(--muted); }
  .hook-select {
    background: #2a2f3a; border: 1px solid var(--border); color: var(--text);
    padding: 8px 10px; border-radius: 6px; font-size: 13px; cursor: pointer;
    max-width: 220px;
  }
  .hook-select:hover { background: #343a48; }
  .spacer { flex: 1; }
  .load-btn, .validate-btn, .new-btn {
    border: none; padding: 10px 18px; border-radius: 6px; font-size: 14px;
    cursor: pointer; font-weight: 600;
  }
  .load-btn, .new-btn { background: #2a2f3a; color: var(--text); }
  .load-btn:hover, .new-btn:hover { background: #343a48; }
  .validate-btn { background: var(--accent); color: #fff; }
  .validate-btn:hover { filter: brightness(1.1); }
  .validate-btn:disabled { opacity: 0.6; cursor: progress; }
  input[type=file] { display: none; }

  .banner {
    margin-top: 18px; padding: 16px 18px; border-radius: 8px;
    font-size: 15px; border: 1px solid;
  }
  .banner.ok   { background: var(--ok-bg);   border-color: var(--ok);   color: var(--ok); }
  .banner.bad  { background: var(--bad-bg);  border-color: var(--bad);  color: var(--bad); }
  .banner.warn { background: var(--warn-bg); border-color: var(--warn); color: var(--warn); }
  .banner .meta { color: var(--muted); font-weight: normal; font-size: 13px; margin-top: 4px; }
  ul.errlist { margin: 10px 0 0; padding-left: 20px; color: var(--text); }
  ul.errlist li { margin-bottom: 4px; font-family: Consolas, Menlo, monospace; font-size: 13.5px; }
  ul.errlist li a { color: inherit; text-decoration: underline dotted; cursor: pointer; }

  .code {
    margin-top: 18px; background: var(--panel); border: 1px solid var(--border);
    border-radius: 10px; overflow: hidden;
  }
  .code .fname {
    padding: 10px 16px; border-bottom: 1px solid var(--border);
    color: var(--muted); font-size: 13px; font-family: Consolas, Menlo, monospace;
    display: flex; justify-content: space-between; align-items: center;
  }
  .dirty-dot {
    display: inline-block; width: 7px; height: 7px; border-radius: 50%;
    background: var(--warn); margin-right: 6px; opacity: 0; transition: opacity 0.15s;
    vertical-align: middle;
  }
  .dirty-dot.show { opacity: 1; }

  .editor-shell { position: relative; }
  .CodeMirror {
    height: 480px; font-family: 'Cascadia Code', Consolas, Menlo, monospace;
    font-size: 13.5px; line-height: 21px; background: #1a1d24 !important;
  }
  .CodeMirror-gutters { background: #14161c !important; border-right: 1px solid var(--border) !important; }
  .CodeMirror-linenumber { color: var(--muted); }
  .CodeMirror-foldgutter-open, .CodeMirror-foldgutter-folded { color: var(--muted); }
  .cm-error-line { background: rgba(255, 93, 93, 0.10); }
  .cm-error-line-gutter { color: var(--bad) !important; font-weight: 700; }
  .CodeMirror-matchingbracket { color: var(--accent) !important; font-weight: 700; }

  .status-bar {
    display: flex; gap: 16px; padding: 6px 16px; background: #12141a;
    border-top: 1px solid var(--border); color: var(--muted); font-size: 12px;
    font-family: Consolas, Menlo, monospace;
  }
  .status-bar .grow { flex: 1; }
  .upload-area {
    margin-top: 18px; padding: 28px; background: var(--panel);
    border: 2px dashed var(--border); border-radius: 10px;
    text-align: center; transition: border-color 0.15s, background 0.15s;
  }
  .upload-area.drag { border-color: var(--accent); background: #181c26; }
  .upload-label { display: block; cursor: pointer; }
  .upload-icon { font-size: 36px; margin-bottom: 6px; }
  .upload-text { font-size: 15px; color: var(--text); }
  .upload-hint { font-size: 13px; color: var(--muted); margin-top: 4px; }
  .bulk-file-list {
    list-style: none; margin: 16px 0 0; padding: 0; text-align: left;
    max-height: 220px; overflow: auto;
    background: #12141a; border: 1px solid var(--border); border-radius: 8px;
  }
  .bulk-file-list:empty { display: none; }
  .bulk-file-list li {
    padding: 6px 12px; font-family: Consolas, Menlo, monospace; font-size: 13px;
    color: var(--text); border-top: 1px solid var(--border);
    display: flex; justify-content: space-between; gap: 12px;
  }
  .bulk-file-list li:first-child { border-top: none; }
  .bulk-file-list li .fsize { color: var(--muted); white-space: nowrap; }
  .bulk-file-list li .fsize-warn { color: var(--warn); }
  .bulk-file-list .bulk-file-summary {
    background: #181c26; color: var(--text); font-weight: 600;
    border-bottom: 1px solid var(--border);
  }
  .bulk-file-list .bulk-file-summary .fsize { font-weight: normal; }

  .bulk-table {
    margin-top: 18px; width: 100%; border-collapse: collapse;
    background: var(--panel); border: 1px solid var(--border);
    border-radius: 10px; overflow: hidden;
  }
  .bulk-table th, .bulk-table td {
    padding: 10px 14px; text-align: left; font-size: 14px;
    border-bottom: 1px solid var(--border);
  }
  .bulk-table th {
    background: #12141a; color: var(--muted); font-weight: 600;
    font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px;
  }
  .bulk-table tr:last-child td { border-bottom: none; }
  .bulk-table tr.row-fail  { background: rgba(255, 93, 93, 0.05); }
  .bulk-table tr.row-error { background: rgba(240, 185, 90, 0.05); }
  .bulk-table .fname-cell {
    font-family: Consolas, Menlo, monospace; font-size: 13px; color: var(--text);
    word-break: break-all;
  }
  .pill {
    display: inline-block; padding: 2px 10px; border-radius: 999px;
    font-size: 12px; font-weight: 600;
  }
  .pill.ok      { background: var(--ok-bg);   color: var(--ok);   border: 1px solid var(--ok); }
  .pill.bad     { background: var(--bad-bg);  color: var(--bad);  border: 1px solid var(--bad); }
  .pill.warn    { background: var(--warn-bg); color: var(--warn); border: 1px solid var(--warn); }
  .pill.pending { background: #12141a; color: var(--muted); border: 1px solid var(--border); }
  .err-toggle { margin: 0; }
  .err-toggle summary { cursor: pointer; color: var(--bad); }
  .err-toggle summary::-webkit-details-marker { color: var(--bad); }
  .err-toggle .errlist { padding-left: 20px; }
</style>
</head>
<body>
<div class="wrap">
  <?php if (!$compilerAccessedDirectly): ?>
    <a class="back-link" href="<?= url('page=home') ?>">&larr; Back to OnlyPlugins</a>
  <?php endif; ?>
  <h1>Oxide Plugin Validator</h1>
  <p class="sub">Load a .cs file (or paste code), pick a branch, and validate against that branch's reference set.</p>

  <nav class="tabs">
    <a class="tab <?= $mode === 'single' ? 'active' : '' ?>" href="<?= $compilerBase ?>mode=single&amp;branch=<?= urlencode($branch) ?>">Single Editor</a>
    <a class="tab <?= $mode === 'bulk'    ? 'active' : '' ?>" href="<?= $compilerBase ?>mode=bulk&amp;branch=<?= urlencode($branch) ?>">Bulk Validator</a>
  </nav>

  <?php if ($mode === 'single'): ?>
    <form id="validateForm" method="post">
      <input type="hidden" name="action" id="actionField" value="validate">
      <input type="hidden" name="mode" value="single">
      <input type="hidden" name="source" id="sourceField">

      <div class="toolbar">
        <div class="branch-group">
          <label><input type="radio" name="branch" value="main" <?= $branch === 'main' ? 'checked' : '' ?>> Main</label>
          <label><input type="radio" name="branch" value="staging" <?= $branch === 'staging' ? 'checked' : '' ?>> Staging</label>
        </div>

        <div class="filename-field">
          File name:
          <input type="text" name="fileName" id="fileNameField" value="<?= htmlspecialchars($fileName) ?>">
        </div>

        <button type="button" class="load-btn" onclick="document.getElementById('fileInput').click()">Load .cs file&hellip;</button>
        <input type="file" id="fileInput" accept=".cs">
        <button type="button" class="new-btn" id="newFileBtn">New</button>

        <span class="hook-picker">
          <input type="text" class="hook-search" id="hookSearch" placeholder="Search hooks&hellip;" autocomplete="off">
          <select class="hook-select" id="hookInsert" title="Insert an Oxide hook or boilerplate snippet at the cursor">
            <option value="">Insert hook&hellip;</option>
            <optgroup label="Lifecycle">
              <option value="init">Init()</option>
              <option value="loaded">Loaded()</option>
              <option value="unload">Unload()</option>
              <option value="serverInit">OnServerInitialized()</option>
              <option value="playerConnected">OnPlayerConnected</option>
            </optgroup>
            <optgroup label="Commands">
              <option value="chatCommand">Chat command</option>
              <option value="consoleCommand">Console command</option>
            </optgroup>
            <optgroup label="Boilerplate">
              <option value="permission">Permission registration</option>
              <option value="config">Config class + LoadDefaultConfig</option>
              <option value="lang">Lang messages + LoadDefaultMessages</option>
              <option value="datafile">Data file load/save</option>
              <option value="pluginRef">[PluginReference]</option>
            </optgroup>
            <!-- Every other hook (660+) is populated at load time from HOOK_DATA,
                 extracted directly from the game's decompiled hook-injection sites,
                 grouped below by category. -->
          </select>
        </span>

        <div class="spacer"></div>

        <button type="submit" class="validate-btn" id="validateBtn" title="Ctrl+Enter">Validate</button>
      </div>
    </form>

    <?php if ($connectError !== null): ?>
      <div class="banner bad">Could not validate: <?= htmlspecialchars($connectError) ?></div>
    <?php endif; ?>

    <?php if ($result !== null): ?>
      <?php if (!empty($result['success'])): ?>
        <div class="banner ok">
          Success &mdash; <?= htmlspecialchars($result['fileName'] ?? $fileName) ?> compiled with no errors against <strong><?= htmlspecialchars($branch) ?></strong>.
          <div class="meta">Compiled in <?= (int)($result['elapsedMilliseconds'] ?? 0) ?> ms</div>
        </div>
      <?php else: ?>
        <div class="banner bad">
          Failed &mdash; <?= htmlspecialchars($result['fileName'] ?? $fileName) ?> has <?= count($result['errors'] ?? []) ?> error(s) against <strong><?= htmlspecialchars($branch) ?></strong>:
          <ul class="errlist">
            <?php foreach ($result['errors'] ?? [] as $err): $ln = (int)($err['line'] ?? 0); ?>
              <li>
                <a href="#" onclick="jumpToLine(<?= $ln ?>); return false;">[<?= $ln ?>:<?= (int)($err['position'] ?? 0) ?>]</a>
                <?= htmlspecialchars($err['message'] ?? 'Unknown error') ?>
              </li>
            <?php endforeach; ?>
          </ul>
        </div>
      <?php endif; ?>
    <?php endif; ?>

    <div class="code">
      <div class="fname">
        <span><span class="dirty-dot" id="dirtyDot"></span><span id="fnameLabel"><?= htmlspecialchars($fileName) ?></span></span>
        <span style="color: var(--muted); font-size: 12px;">Ctrl+Enter validate &middot; Ctrl+F find</span>
      </div>
      <div class="editor-shell">
        <textarea id="source" name="source_display" spellcheck="false" placeholder="Paste your plugin code here, or load a .cs file above."><?= htmlspecialchars($source) ?></textarea>
      </div>
      <div class="status-bar">
        <span id="statusPos">Ln 1, Col 1</span>
        <span id="statusLen">0 chars</span>
        <span class="grow"></span>
        <span id="statusFile"><?= htmlspecialchars($fileName) ?></span>
      </div>
    </div>

  <?php elseif ($mode === 'bulk'): ?>
    <form id="bulkForm" method="post" enctype="multipart/form-data">
      <div class="toolbar">
        <div class="branch-group">
          <label><input type="radio" name="branch" value="main" <?= $branch === 'main' ? 'checked' : '' ?>> Main</label>
          <label><input type="radio" name="branch" value="staging" <?= $branch === 'staging' ? 'checked' : '' ?>> Staging</label>
        </div>
        <div class="spacer"></div>
        <button type="submit" class="validate-btn" id="bulkSubmit">Validate All</button>
      </div>

      <div class="upload-area" id="bulkDrop">
        <label for="bulkFileInput" class="upload-label">
          <div class="upload-icon">📁</div>
          <div class="upload-text">Click to pick .cs files, or drop them here</div>
          <div class="upload-hint">
            Up to <strong><?= number_format(MAX_BULK_FILES) ?></strong> files
            &middot; <?= MAX_UPLOAD_BYTES / 1024 / 1024 ?> MB each
            &middot; <?= BULK_CONCURRENCY ?> in parallel (matches the compiler's <?= BULK_CONCURRENCY ?> threads per branch)
          </div>
        </label>
        <input type="file" id="bulkFileInput" name="files[]" accept=".cs" multiple>
        <ul id="bulkFileList" class="bulk-file-list"></ul>
      </div>
    </form>

    <div id="bulkSummary" class="banner" style="display: none;"></div>

    <table class="bulk-table" id="bulkTableWrap" style="display: none;">
      <thead>
        <tr>
          <th style="width: 45%;">File</th>
          <th style="width: 15%;">Status</th>
          <th style="width: 30%;">Errors</th>
          <th style="width: 10%;">Elapsed</th>
        </tr>
      </thead>
      <tbody id="bulkTableBody"></tbody>
    </table>

  <?php endif; ?>
</div>

<script>
const errorLines    = <?= json_encode(array_keys($errorsByLine)) ?>;
const textarea      = document.getElementById('source');
const fileInput     = document.getElementById('fileInput');
const fileNameField = document.getElementById('fileNameField');
const fnameLabel    = document.getElementById('fnameLabel');
const statusFile    = document.getElementById('statusFile');
const form          = document.getElementById('validateForm');
const sourceField   = document.getElementById('sourceField');
const actionField   = document.getElementById('actionField');
const dirtyDot      = document.getElementById('dirtyDot');
const newFileBtn    = document.getElementById('newFileBtn');
const hookInsert    = document.getElementById('hookInsert');
const hookSearch    = document.getElementById('hookSearch');

function toClassName(name) {
  let base = String(name || '').trim().replace(/\.cs$/i, '');
  const words = base.split(/[^A-Za-z0-9]+/).filter(Boolean);
  let className = words.map(w => w.charAt(0).toUpperCase() + w.slice(1)).join('');
  className = className.replace(/[^A-Za-z0-9_]/g, '');
  if (className === '') className = 'NewPlugin';
  if (/^[0-9]/.test(className)) className = '_' + className;
  return className;
}
function toInfoName(name) {
  let base = String(name || '').trim().replace(/\.cs$/i, '');
  base = base.replace(/[_-]+/g, ' ');
  base = base.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  base = base.replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
  base = base.replace(/\s+/g, ' ').trim();
  return base === '' ? 'New Plugin' : base;
}
function escAttr(s) {
  return String(s || '').replace(/["\\]/g, '');
}

function buildNewPluginTemplate(className, infoName, author) {
  return `using Oxide.Core;
using Oxide.Core.Plugins;

namespace Oxide.Plugins;

[Info("${escAttr(infoName)}", "${escAttr(author)}", "1.0.0")]
[Description("Description of what this plugin does.")]
public class ${className} : RustPlugin
{
    private void Init()
    {
    }
}
`;
}

let lastGeneratedTemplate = null;
let lastGeneratedAuthor   = 'YourName';

const SNIPPETS = {
  init: { code: `    private void Init()
    {
    }` },
  loaded: { code: `    private void Loaded()
    {
    }` },
  unload: { code: `    private void Unload()
    {
    }` },
  serverInit: { code: `    private void OnServerInitialized()
    {
    }` },
  playerConnected: { code: `    private void OnPlayerConnected(BasePlayer player)
    {
    }` },
  chatCommand: { code: `    [ChatCommand("mycommand")]
    private void MyCommandChat(BasePlayer player, string command, string[] args)
    {
        player.ChatMessage("Hello from mycommand!");
    }` },
  consoleCommand: { code: `    [ConsoleCommand("mycommand")]
    private void MyCommandConsole(ConsoleSystem.Arg arg)
    {
        var player = arg.Player();
        if (player == null) return; 
    }` },
  permission: { code: `    private const string PermissionUse = "myplugin.use";

    private void Init()
    {
        permission.RegisterPermission(PermissionUse, this);
    }` },
  config: { code: `    private Configuration config;

    private class Configuration
    {
        [JsonProperty("Example setting")]
        public bool ExampleSetting = true;
    }

    protected override void LoadDefaultConfig() => config = new Configuration();

    protected override void LoadConfig()
    {
        base.LoadConfig();
        config = Config.ReadObject<Configuration>();
    }

    protected override void SaveConfig() => Config.WriteObject(config);` },
  lang: { code: `    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            ["ExampleMessage"] = "Hello, {0}!"
        }, this);
    }

    private string Msg(string key, string userId = null, params object[] args) =>
        string.Format(lang.GetMessage(key, this, userId), args);` },
  datafile: { code: `    private DynamicConfigFile dataFile;
    private Dictionary<string, object> data;

    private void LoadData()
    {
        dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);
        data = dataFile.ReadObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
    }

    private void SaveData() => dataFile.WriteObject(data);` },
  pluginRef: { code: `    [PluginReference]
    private Plugin OtherPlugin;` },
};

const HOOK_DATA = [{"n":"OnTick","c":"Server","p":{},"rb":0,"rt":null},{"n":"OnPlayerDisconnected","c":"Player","p":{"basePlayer":"BasePlayer","strReason":"string"},"rb":0,"rt":null},{"n":"OnItemRemovedFromContainer","c":"Item","p":{"instance":"ItemContainer","item":"Item"},"rb":0,"rt":null},{"n":"OnItemAddedToContainer","c":"Item","p":{"instance":"ItemContainer","item":"Item"},"rb":0,"rt":null},{"n":"OnEntitySpawned","c":"Entity","p":{"instance":"BaseNetworkable"},"rb":0,"rt":null},{"n":"CanUseLockedEntity","c":"Player","p":{"player":"BasePlayer","instance":"CodeLock"},"rb":1,"rt":"bool"},{"n":"OnItemCraft","c":"Item","p":{"itemCraftTask":"ItemCraftTask","owner":"BasePlayer","fromTempBlueprint":"Item"},"rb":1,"rt":"bool"},{"n":"OnLootEntity","c":"Player","p":{"instance":"PlayerLoot","targetEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnLootItem","c":"Player","p":{"instance":"PlayerLoot","item":"Item"},"rb":0,"rt":null},{"n":"OnEntityEnter","c":"Entity","p":{"instance":"TriggerBase","ent":"BaseEntity"},"rb":1,"rt":null},{"n":"OnEntityLeave","c":"Entity","p":{"instance":"TriggerBase","ent":"BaseEntity"},"rb":1,"rt":null},{"n":"OnItemDeployed","c":"Item","p":{"instance":"Deployer","modDeployable":"ItemModDeployable","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"IOnBaseCombatEntityHurt","c":"Entity","p":{"instance":"BaseCombatEntity","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnDispenserGather","c":"Resource","p":{"instance":"ResourceDispenser","entity":"BasePlayer","item":"Item"},"rb":1,"rt":null},{"n":"OnPlayerAttack","c":"Player","p":{"instance":"BaseMelee","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnRunPlayerMetabolism","c":"Player","p":{"instance":"PlayerMetabolism","ownerEntity":"BaseCombatEntity","delta":"float"},"rb":1,"rt":null},{"n":"IOnUserApprove","c":"Player","p":{"connection":"Network.Connection"},"rb":1,"rt":null},{"n":"OnWallpaperSet","c":"Structure","p":{"instance":"BuildingBlock","id":"ulong","side":"int","rotation":"float"},"rb":1,"rt":null},{"n":"OnWallpaperRemove","c":"Structure","p":{"instance":"BuildingBlock","side":"int"},"rb":1,"rt":null},{"n":"OnStructureUpgrade","c":"Structure","p":{"instance":"BuildingBlock","player":"BasePlayer","type":"BuildingGrade.Enum","skin":"ulong"},"rb":1,"rt":null},{"n":"OnStructureDemolish","c":"Structure","p":{"instance":"DecayEntity","player":"BasePlayer","true":"bool"},"rb":1,"rt":null},{"n":"OnStructureRotate","c":"Structure","p":{"instance":"BuildingBlock","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnFuelConsume","c":"Fuel","p":{"instance":"BaseOven","fuel":"Item","burnable":"ItemModBurnable"},"rb":1,"rt":null},{"n":"CanUpdateSign","c":"Player","p":{"player":"BasePlayer","instance":"Signage"},"rb":1,"rt":"bool"},{"n":"OnSignLocked","c":"Structure","p":{"instance":"Signage","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnSignUpdated","c":"Structure","p":{"instance":"Signage","player":"BasePlayer","num":"int"},"rb":0,"rt":null},{"n":"IOnLoseCondition","c":"Item","p":{"instance":"Item","amount":"float"},"rb":1,"rt":null},{"n":"OnPlayerSleepEnd","c":"Player","p":{"instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnEntityGroundMissing","c":"Entity","p":{"baseEntity":"BaseEntity"},"rb":1,"rt":null},{"n":"OnDoorOpened","c":"Structure","p":{"instance":"Door","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnDoorClosed","c":"Structure","p":{"instance":"Door","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerTick","c":"Player","p":{"instance":"BasePlayer","msg":"PlayerTick","wasPlayerStalled":"bool"},"rb":1,"rt":null},{"n":"IOnBasePlayerAttacked","c":"Player","p":{"instance":"BasePlayer","info":"HitInfo"},"rb":1,"rt":null},{"n":"IOnBasePlayerHurt","c":"Player","p":{"instance":"BasePlayer","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnExplosiveThrown","c":"Weapon","p":{"player":"BasePlayer","baseEntity":"BaseEntity","instance":"ThrownWeapon"},"rb":0,"rt":null},{"n":"OnMeleeThrown","c":"Weapon","p":{"player":"BasePlayer","item":"Item"},"rb":0,"rt":null},{"n":"OnItemCraftFinished","c":"Item","p":{"task":"ItemCraftTask","item":"Item","instance":"ItemCrafter"},"rb":0,"rt":null},{"n":"OnHealingItemUse","c":"Item","p":{"instance":"MedicalTool","fromPlayer":"BasePlayer","toTarget":"IMedicalToolTarget"},"rb":1,"rt":null},{"n":"CanResearchItem","c":"Player","p":{"player":"BasePlayer","targetItem":"Item"},"rb":1,"rt":null},{"n":"OnItemResearch","c":"Item","p":{"instance":"ResearchTable","targetItem":"Item","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnItemResearched","c":"Item","p":{"instance":"ResearchTable","num":"int"},"rb":3,"rt":"int"},{"n":"CanLootPlayer","c":"Player","p":{"instance":"BasePlayer","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"CanBeWounded","c":"Player","p":{"instance":"BasePlayer","info":"HitInfo"},"rb":1,"rt":"bool"},{"n":"OnRocketLaunched","c":"Weapon","p":{"player":"BasePlayer","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnWeaponFired","c":"Weapon","p":{"instance":"BaseProjectile","player":"BasePlayer","component":"ItemModProjectile","projectileShoot":"ProtoBuf.ProjectileShoot"},"rb":0,"rt":null},{"n":"OnItemUse","c":"Item","p":{"instance":"Item","amountToConsume":"int"},"rb":3,"rt":"int"},{"n":"OnHammerHit","c":"Structure","p":{"ownerPlayer":"BasePlayer","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnSurveyGather","c":"Resource","p":{"instance":"SurveyCharge","itemManager":"ItemManager"},"rb":0,"rt":null},{"n":"OnAirdrop","c":"Entity","p":{"instance":"CargoPlane","newDropPosition":"UnityEngine.Vector3"},"rb":0,"rt":null},{"n":"OnStructureRepair","c":"Structure","p":{"instance":"BaseCombatEntity","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnItemRepair","c":"Item","p":{"player":"BasePlayer","itemToRepair":"Item"},"rb":1,"rt":null},{"n":"OnTrapSnapped","c":"Traps","p":{"instance":"BaseTrapTrigger","obj":"UnityEngine.GameObject","col":"UnityEngine.Collider"},"rb":0,"rt":null},{"n":"OnTrapDisarm","c":"Traps","p":{"instance":"Landmine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTrapArm","c":"Traps","p":{"instance":"BearTrap","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnMapImageUpdated","c":"Item","p":{},"rb":0,"rt":null},{"n":"OnItemCraftCancelled","c":"Item","p":{"itemCraftTask":"ItemCraftTask","instance":"ItemCrafter"},"rb":0,"rt":null},{"n":"OnResourceDepositCreated","c":"Resource","p":{"resourceDeposit":"ResourceDepositManager.ResourceDeposit"},"rb":0,"rt":null},{"n":"OnItemUpgrade","c":"Item","p":{"item":"Item","item2":"Item","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnCupboardDeauthorize","c":"Structure","p":{"instance":"BuildingPrivlidge","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanNetworkTo","c":"Network","p":{"instance":"BaseNetworkable","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnTurretTarget","c":"Turret","p":{"instance":"AutoTurret","targ":"BaseCombatEntity"},"rb":1,"rt":null},{"n":"CanBeTargeted","c":"Player","p":{"obj":"BaseCombatEntity","instance":"AutoTurret"},"rb":1,"rt":"bool"},{"n":"OnHelicopterTarget","c":"Vehicle","p":{"instance":"HelicopterTurret","newTarget":"BaseCombatEntity"},"rb":1,"rt":null},{"n":"CanBuild","c":"Structure","p":{"instance":"Planner","construction":"Construction","target":"Construction.Target"},"rb":1,"rt":null},{"n":"CanEquipItem","c":"Item","p":{"instance":"PlayerInventory","item":"Item","targetSlot":"int"},"rb":1,"rt":"bool"},{"n":"CanWearItem","c":"Item","p":{"instance":"PlayerInventory","item":"Item","targetSlot":"int"},"rb":1,"rt":"bool"},{"n":"CanAcceptItem","c":"Item","p":{"instance":"ItemContainer","item":"Item","targetPos":"int"},"rb":1,"rt":"ItemContainer.CanAcceptResult"},{"n":"OnPlayerLootEnd","c":"Player","p":{"instance":"PlayerLoot"},"rb":0,"rt":null},{"n":"OnItemSplit","c":"Item","p":{"instance":"Item","split_Amount":"int"},"rb":1,"rt":"Item"},{"n":"OnCupboardClearList","c":"Structure","p":{"instance":"BuildingPrivlidge","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnLootEntityEnd","c":"Player","p":{"player":"BasePlayer","instance":"LootableCorpse"},"rb":0,"rt":null},{"n":"CanCreateWorldProjectile","c":"Weapon","p":{"info":"HitInfo","itemDef":"ItemDefinition"},"rb":1,"rt":null},{"n":"OnItemPickup","c":"Item","p":{"item":"Item","player":"BasePlayer","instance":"WorldItem"},"rb":1,"rt":null},{"n":"CanBypassQueue","c":"Player","p":{"connection":"Network.Connection"},"rb":1,"rt":"bool"},{"n":"OnEntityKill","c":"Entity","p":{"instance":"BaseNetworkable"},"rb":1,"rt":null},{"n":"OnPlayerRespawned","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMessagePlayer","c":"Server","p":{"msg":"string","instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnServerMessage","c":"Server","p":{"message":"string","username":"string","color":"string","userid":"ulong"},"rb":1,"rt":null},{"n":"OnPlayerActionBroadcast","c":"Server","p":{"subject":"BasePlayer","action":"string"},"rb":1,"rt":null},{"n":"OnRconConnection","c":"Server","p":{"Address":"System.Net.IPAddress"},"rb":1,"rt":null},{"n":"OnClientAuth","c":"Player","p":{"connection":"Network.Connection"},"rb":0,"rt":null},{"n":"OnNewSave","c":"Server","p":{"strFilename":"string"},"rb":0,"rt":null},{"n":"IOnServerShutdown","c":"Server","p":{},"rb":0,"rt":null},{"n":"OnSaveLoad","c":"Server","p":{"dictionary`2":"Dictionary<BaseEntity, ProtoBuf.Entity>"},"rb":1,"rt":"bool"},{"n":"OnPlayerSpectate","c":"Player","p":{"instance":"BasePlayer","spectateFilter":"string"},"rb":1,"rt":null},{"n":"OnPlayerSpectateEnd","c":"Player","p":{"instance":"BasePlayer","spectateFilter":"string"},"rb":1,"rt":null},{"n":"OnPlayerHealthChange","c":"Player","p":{"instance":"BasePlayer","oldvalue":"float","newvalue":"float"},"rb":1,"rt":null},{"n":"OnTurretStartup","c":"Turret","p":{"instance":"AutoTurret"},"rb":1,"rt":null},{"n":"OnTurretShutdown","c":"Turret","p":{"instance":"AutoTurret"},"rb":1,"rt":null},{"n":"OnTurretToggle","c":"Turret","p":{"instance":"AutoTurret"},"rb":1,"rt":null},{"n":"OnPlayerSleep","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerDeath","c":"Player","p":{"instance":"BasePlayer","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnOvenToggle","c":"Entity","p":{"instance":"BaseOven","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTrapTrigger","c":"Traps","p":{"instance":"BearTrap","obj":"UnityEngine.GameObject"},"rb":1,"rt":null},{"n":"OnTerrainInitialized","c":"World","p":{},"rb":0,"rt":null},{"n":"OnFindBurnable","c":"Item","p":{"instance":"BaseOven"},"rb":1,"rt":"Item"},{"n":"CanStackItem","c":"Item","p":{"instance":"Item","item":"Item"},"rb":1,"rt":"bool"},{"n":"OnExplosiveDropped","c":"Weapon","p":{"player":"BasePlayer","baseEntity":"BaseEntity","instance":"ThrownWeapon"},"rb":0,"rt":null},{"n":"OnBuyVendingItem","c":"Vending","p":{"instance":"VendingMachine","player":"BasePlayer","num":"int","num2":"int"},"rb":1,"rt":null},{"n":"CanUseVending","c":"Vending","p":{"player":"BasePlayer","instance":"VendingMachine"},"rb":1,"rt":"bool"},{"n":"CanAdministerVending","c":"Vending","p":{"player":"BasePlayer","instance":"VendingMachine"},"rb":1,"rt":"bool"},{"n":"OnRefreshVendingStock","c":"Vending","p":{"instance":"VendingMachine","itemDef":"ItemDefinition"},"rb":0,"rt":null},{"n":"OnToggleVendingBroadcast","c":"Vending","p":{"instance":"VendingMachine","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnDeleteVendingOffer","c":"Vending","p":{"instance":"VendingMachine","num":"int"},"rb":0,"rt":null},{"n":"OnOpenVendingAdmin","c":"Vending","p":{"instance":"VendingMachine","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnVendingShopOpen","c":"Vending","p":{"instance":"VendingMachine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnRotateVendingMachine","c":"Vending","p":{"instance":"VendingMachine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanPickupEntity","c":"Player","p":{"player":"BasePlayer","instance":"BaseCombatEntity"},"rb":1,"rt":"bool"},{"n":"OnServerUserSet","c":"Server","p":{"uid":"ulong","group":"ServerUsers.UserGroup","username":"string","notes":"string","expiry":"long"},"rb":0,"rt":null},{"n":"OnServerSave","c":"Server","p":{},"rb":0,"rt":null},{"n":"OnItemAction","c":"Item","p":{"item":"Item","text":"string","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanAssignBed","c":"Player","p":{"player":"BasePlayer","instance":"SleepingBag","num":"ulong"},"rb":1,"rt":null},{"n":"OnCodeEntered","c":"Structure","p":{"instance":"CodeLock","player":"BasePlayer","text":"string"},"rb":1,"rt":null},{"n":"CanUnlock","c":"Player","p":{"player":"BasePlayer","instance":"CodeLock"},"rb":1,"rt":null},{"n":"CanLock","c":"Vehicle","p":{"player":"BasePlayer","owner":"ModularCar","instance":"ModularCarCodeLock"},"rb":1,"rt":"bool"},{"n":"CanChangeCode","c":"Player","p":{"player":"BasePlayer","instance":"CodeLock","text":"string","flag":"bool"},"rb":1,"rt":null},{"n":"OnRecyclerToggle","c":"Entity","p":{"instance":"Recycler","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanRecycle","c":"Crafting","p":{"instance":"Recycler","slot":"Item"},"rb":1,"rt":"bool"},{"n":"OnItemRecycle","c":"Item","p":{"slot":"Item","instance":"Recycler"},"rb":1,"rt":null},{"n":"OnTurretDeauthorize","c":"Turret","p":{"instance":"AutoTurret","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanSetBedPublic","c":"Player","p":{"player":"BasePlayer","instance":"SleepingBag"},"rb":1,"rt":null},{"n":"CanCraft","c":"Crafting","p":{"instance":"ItemCrafter","bp":"ItemBlueprint","amount":"int","free":"bool"},"rb":1,"rt":"bool"},{"n":"CanHelicopterStrafeTarget","c":"Vehicle","p":{"instance":"PatrolHelicopterAI","ply":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnItemDropped","c":"Item","p":{"instance":"Item","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"CanMoveItem","c":"Item","p":{"item":"Item","instance":"PlayerInventory","itemContainerId":"ItemContainerId","num":"int","num2":"int","itemMoveModifier":"ItemMoveModifier"},"rb":1,"rt":null},{"n":"CanHideStash","c":"Player","p":{"player":"BasePlayer","instance":"StashContainer"},"rb":1,"rt":null},{"n":"CanCombineDroppedItem","c":"Item","p":{"instance":"DroppedItem","di":"DroppedItem"},"rb":1,"rt":null},{"n":"OnContainerDropItems","c":"Entity","p":{"container":"ItemContainer"},"rb":1,"rt":null},{"n":"CanNpcEat","c":"NPC","p":{"instance":"BaseNpc","best":"BaseEntity"},"rb":1,"rt":"bool"},{"n":"OnNpcAttack","c":"NPC","p":{"instance":"BaseNpc","AttackTarget":"BaseEntity"},"rb":1,"rt":null},{"n":"OnMeleeAttack","c":"Player","p":{"player":"BasePlayer","hitInfo":"HitInfo"},"rb":1,"rt":null},{"n":"OnPlayerViolation","c":"Player","p":{"ply":"BasePlayer","type":"AntiHackType","amount":"float","gameObject":"UnityEngine.GameObject"},"rb":1,"rt":null},{"n":"CanChangeGrade","c":"Structure","p":{"player":"BasePlayer","instance":"BuildingBlock","iGrade":"BuildingGrade.Enum","iSkin":"ulong"},"rb":1,"rt":"bool"},{"n":"CanAffordUpgrade","c":"Structure","p":{"player":"BasePlayer","instance":"BuildingBlock","iGrade":"BuildingGrade.Enum","iSkin":"ulong"},"rb":1,"rt":"bool"},{"n":"CanDemolish","c":"Structure","p":{"player":"BasePlayer","instance":"DecayEntity"},"rb":1,"rt":"bool"},{"n":"CanUseMailbox","c":"Player","p":{"player":"BasePlayer","instance":"Mailbox"},"rb":1,"rt":"bool"},{"n":"OnSpinWheel","c":"Player","p":{"player":"BasePlayer","instance":"SpinnerWheel"},"rb":1,"rt":null},{"n":"IOnRconInitialize","c":"Server","p":{},"rb":1,"rt":null},{"n":"OnMaxStackable","c":"Item","p":{"instance":"Item"},"rb":1,"rt":"int"},{"n":"OnWeaponReload","c":"Weapon","p":{"instance":"BaseProjectile","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPlayerRespawn","c":"Player","p":{"instance":"BasePlayer","spawnPoint":"BasePlayer.SpawnPoint"},"rb":3,"rt":"BasePlayer.SpawnPoint"},{"n":"CanPickupLock","c":"Player","p":{"player":"BasePlayer","instance":"BaseLock"},"rb":1,"rt":null},{"n":"OnDispenserBonus","c":"Resource","p":{"instance":"ResourceDispenser","player":"BasePlayer","item2":"Item"},"rb":3,"rt":"Item"},{"n":"CanVendingAcceptItem","c":"Vending","p":{"instance":"VendingMachine","item":"Item","targetSlot":"int"},"rb":1,"rt":"bool"},{"n":"OnLiftUse","c":"Elevator","p":{"instance":"Lift","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnServerUserRemove","c":"Server","p":{"uid":"ulong"},"rb":0,"rt":null},{"n":"OnPlayerKicked","c":"Player","p":{"instance":"BasePlayer","reason":"string","reserveSlot":"bool"},"rb":0,"rt":null},{"n":"CanBradleyApcTarget","c":"Vehicle","p":{"instance":"BradleyAPC","ent":"BaseEntity"},"rb":1,"rt":"bool"},{"n":"OnBradleyApcInitialize","c":"Vehicle","p":{"instance":"BradleyAPC"},"rb":1,"rt":null},{"n":"OnBradleyApcHunt","c":"Vehicle","p":{"instance":"BradleyAPC"},"rb":1,"rt":null},{"n":"OnBradleyApcPatrol","c":"Vehicle","p":{"instance":"BradleyAPC"},"rb":1,"rt":null},{"n":"OnShopCompleteTrade","c":"Shop","p":{"instance":"ShopFront"},"rb":1,"rt":null},{"n":"CanHelicopterUseNapalm","c":"Vehicle","p":{"instance":"PatrolHelicopterAI"},"rb":1,"rt":"bool"},{"n":"CanHelicopterStrafe","c":"Vehicle","p":{"instance":"PatrolHelicopterAI"},"rb":1,"rt":"bool"},{"n":"CanHelicopterTarget","c":"Vehicle","p":{"instance":"PatrolHelicopterAI","ply":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"CanDismountEntity","c":"Player","p":{"player":"BasePlayer","instance":"BaseMountable"},"rb":1,"rt":null},{"n":"OnEntityDismounted","c":"Entity","p":{"instance":"BaseMountable","player":"BasePlayer"},"rb":0,"rt":null},{"n":"CanMountEntity","c":"Player","p":{"player":"BasePlayer","instance":"BaseMountable"},"rb":1,"rt":null},{"n":"OnEntityMounted","c":"Player","p":{"instance":"BaseMountable","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnResearchCostDetermine","c":"Item","p":{"item":"Item"},"rb":1,"rt":"int"},{"n":"OnLootSpawn","c":"Resource","p":{"instance":"LootContainer"},"rb":1,"rt":null},{"n":"CanDropActiveItem","c":"Player","p":{"instance":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnPlayerActiveShieldDrop","c":"Player","p":{"player":"BasePlayer","shield":"Shield"},"rb":1,"rt":null},{"n":"OnTurretClearList","c":"Turret","p":{"instance":"AutoTurret","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTurretModeToggle","c":"Turret","p":{"instance":"AutoTurret","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnWorldProjectileCreate","c":"Weapon","p":{"info":"HitInfo","item":"Item"},"rb":1,"rt":null},{"n":"OnActiveItemChange","c":"Player","p":{"instance":"BasePlayer","activeItem":"Item","itemID":"ItemId"},"rb":1,"rt":null},{"n":"OnAmmoSwitch","c":"Weapon","p":{"instance":"BaseProjectile","basePlayer":"BasePlayer","itemDefinition":"ItemDefinition"},"rb":1,"rt":null},{"n":"CanLootEntity","c":"Entity","p":{"player":"BasePlayer","instance":"WorldItem"},"rb":1,"rt":null},{"n":"OnEntityBuilt","c":"Structure","p":{"instance":"Planner","gameObject":"UnityEngine.GameObject"},"rb":0,"rt":null},{"n":"OnDoorKnocked","c":"Structure","p":{"instance":"Door","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerInput","c":"Player","p":{"instance":"BasePlayer","serverInput":"InputState"},"rb":1,"rt":null},{"n":"IOnNpcTarget","c":"NPC","p":{"instance":"BaseNpc","target":"BaseEntity"},"rb":1,"rt":"float"},{"n":"CanHackCrate","c":"Player","p":{"player":"BasePlayer","instance":"HackableLockedCrate"},"rb":1,"rt":null},{"n":"OnCrateHack","c":"Entity","p":{"instance":"HackableLockedCrate"},"rb":0,"rt":null},{"n":"OnCrateHackEnd","c":"Entity","p":{"instance":"HackableLockedCrate"},"rb":0,"rt":null},{"n":"OnCrateLanded","c":"Entity","p":{"instance":"HackableLockedCrate"},"rb":0,"rt":null},{"n":"OnCrateDropped","c":"Entity","p":{"instance":"HackableLockedCrate"},"rb":0,"rt":null},{"n":"OnExperimentStart","c":"Player","p":{"instance":"Workbench","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanHelicopterDropCrate","c":"Vehicle","p":{"instance":"CH47HelicopterAIController"},"rb":1,"rt":"bool"},{"n":"OnHelicopterDropCrate","c":"Vehicle","p":{"instance":"CH47HelicopterAIController"},"rb":0,"rt":null},{"n":"OnHelicopterAttack","c":"Vehicle","p":{"instance":"CH47HelicopterAIController","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnEntityDestroy","c":"Entity","p":{"instance":"CH47HelicopterAIController"},"rb":1,"rt":null},{"n":"OnHelicopterOutOfCrates","c":"Vehicle","p":{"instance":"CH47HelicopterAIController"},"rb":1,"rt":"bool"},{"n":"OnHelicopterDropDoorOpen","c":"Vehicle","p":{"instance":"CH47HelicopterAIController"},"rb":1,"rt":null},{"n":"CanRenameBed","c":"Player","p":{"player":"BasePlayer","instance":"SleepingBag","text":"string"},"rb":1,"rt":null},{"n":"OnAddVendingOffer","c":"Vending","p":{"instance":"VendingMachine","sellOrder":"ProtoBuf.VendingMachine.SellOrder"},"rb":0,"rt":null},{"n":"OnLootPlayer","c":"Player","p":{"instance":"BasePlayer","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnFlameThrowerBurn","c":"Weapon","p":{"instance":"FlameThrower","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnFireBallDamage","c":"Weapon","p":{"instance":"FireBall","baseCombatEntity":"BaseCombatEntity","hitInfo":"HitInfo"},"rb":0,"rt":null},{"n":"OnFireBallSpread","c":"Weapon","p":{"instance":"FireBall","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnPlayerMetabolize","c":"Player","p":{"instance":"PlayerMetabolism","ownerEntity":"BaseCombatEntity","delta":"float"},"rb":0,"rt":null},{"n":"OnEntityMarkHostile","c":"Entity","p":{"instance":"BaseCombatEntity","duration":"float"},"rb":1,"rt":null},{"n":"OnArcadeScoreAdded","c":"Entity","p":{"instance":"BaseArcadeMachine","player":"BasePlayer","score":"int"},"rb":0,"rt":null},{"n":"CanEntityBeHostile","c":"Entity","p":{"instance":"BaseCombatEntity"},"rb":1,"rt":"bool"},{"n":"OnItemRemove","c":"Item","p":{"instance":"Item"},"rb":1,"rt":null},{"n":"ICanPickupEntity","c":"Player","p":{"player":"BasePlayer","instance":"DoorCloser"},"rb":1,"rt":null},{"n":"OnTeamCreate","c":"Team","p":{"player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTeamRejectInvite","c":"Team","p":{"basePlayer":"BasePlayer","playerTeam":"RelationshipManager.PlayerTeam"},"rb":1,"rt":null},{"n":"OnTeamLeave","c":"Team","p":{"playerTeam":"RelationshipManager.PlayerTeam","basePlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTeamKick","c":"Team","p":{"playerTeam":"RelationshipManager.PlayerTeam","basePlayer":"BasePlayer","uLong":"ulong"},"rb":1,"rt":null},{"n":"OnTeamAcceptInvite","c":"Team","p":{"playerTeam":"RelationshipManager.PlayerTeam","basePlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTeamDisband","c":"Team","p":{"teamToDisband":"RelationshipManager.PlayerTeam"},"rb":1,"rt":null},{"n":"OnTeamCreated","c":"Team","p":{"player":"BasePlayer","playerTeam":"RelationshipManager.PlayerTeam"},"rb":0,"rt":null},{"n":"OnTeamDisbanded","c":"Team","p":{"teamToDisband":"RelationshipManager.PlayerTeam"},"rb":0,"rt":null},{"n":"OnPlayerLand","c":"Player","p":{"instance":"BasePlayer","num":"float"},"rb":1,"rt":null},{"n":"OnPlayerLanded","c":"Player","p":{"instance":"BasePlayer","num":"float"},"rb":0,"rt":null},{"n":"CanDeployItem","c":"Item","p":{"player":"BasePlayer","instance":"Deployer","networkableId":"NetworkableId"},"rb":1,"rt":null},{"n":"CanSamSiteShoot","c":"Entity","p":{"instance":"SamSite"},"rb":1,"rt":null},{"n":"OnWorldPrefabSpawned","c":"World","p":{"gameObject":"UnityEngine.GameObject","category":"string"},"rb":0,"rt":null},{"n":"OnBoatPathGenerate","c":"Vehicle","p":{"instance":"BaseBoat"},"rb":1,"rt":"List<Vector3>"},{"n":"OnCollectiblePickup","c":"Resource","p":{"instance":"CollectibleEntity","reciever":"BasePlayer","eat":"bool"},"rb":1,"rt":null},{"n":"OnProjectileRicochet","c":"Weapon","p":{"instance":"BasePlayer","playerProjectileRicochet":"ProtoBuf.PlayerProjectileRicochet"},"rb":1,"rt":null},{"n":"CanAffordToPlace","c":"Structure","p":{"ownerPlayer":"BasePlayer","instance":"Planner","component":"Construction"},"rb":1,"rt":"bool"},{"n":"CanSpectateTarget","c":"Player","p":{"instance":"BasePlayer","strName":"string"},"rb":1,"rt":null},{"n":"OnSwitchToggle","c":"Entity","p":{"instance":"ElectricSwitch","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnShopAcceptClick","c":"Shop","p":{"instance":"ShopFront","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnShopCancelClick","c":"Shop","p":{"instance":"ShopFront","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnGiveSoldItem","c":"Vending","p":{"instance":"VendingMachine","soldItem":"Item","buyer":"BasePlayer"},"rb":1,"rt":null},{"n":"CanTakeCutting","c":"Player","p":{"player":"BasePlayer","instance":"GrowableEntity"},"rb":1,"rt":null},{"n":"OnSendCommand","c":"Server","p":{"cn":"Network.Connection","strCommand":"string","args":"object[]"},"rb":1,"rt":null},{"n":"OnBroadcastCommand","c":"Server","p":{"strCommand":"string","args":"object[]"},"rb":1,"rt":null},{"n":"OnVendingShopRename","c":"Vending","p":{"instance":"VendingMachine","obj":"string","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnNpcGiveSoldItem","c":"NPC","p":{"instance":"NPCVendingMachine","soldItem":"Item","buyer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnExcavatorGather","c":"Resource","p":{"instance":"ExcavatorArm","item":"Item"},"rb":1,"rt":null},{"n":"OnEntityTakeDamage","c":"Entity","p":{"instance":"ResourceEntity","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnSupplyDropLanded","c":"Entity","p":{"instance":"SupplyDrop"},"rb":0,"rt":null},{"n":"OnHelicopterStrafeEnter","c":"Vehicle","p":{"instance":"PatrolHelicopterAI","position":"UnityEngine.Vector3","strafeTarget":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPlayerSetInfo","c":"Player","p":{"connection":"Network.Connection","key":"string","val":"string"},"rb":0,"rt":null},{"n":"OnTakeCurrencyItem","c":"Vending","p":{"instance":"VendingMachine","takenCurrencyItem":"Item"},"rb":1,"rt":null},{"n":"OnEntityStabilityCheck","c":"Entity","p":{"instance":"StabilityEntity"},"rb":1,"rt":null},{"n":"OnPayForUpgrade","c":"Player","p":{"player":"BasePlayer","instance":"BuildingBlock","g":"ConstructionGrade"},"rb":1,"rt":null},{"n":"OnPayForPlacement","c":"Player","p":{"player":"BasePlayer","instance":"Planner","component":"Construction"},"rb":1,"rt":null},{"n":"OnEntityDeath","c":"Entity","p":{"instance":"ResourceEntity","info":"HitInfo"},"rb":0,"rt":null},{"n":"OnTeamUpdate","c":"Team","p":{"currentTeam":"ulong","newTeam":"ulong","instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTeamUpdated","c":"Team","p":{"currentTeam":"ulong","playerTeam2":"ProtoBuf.PlayerTeam","instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnExplosiveFuseSet","c":"Weapon","p":{"instance":"TimedExplosive","fuseLength":"float"},"rb":3,"rt":"float"},{"n":"OnPlayerAssist","c":"Player","p":{"instance":"BasePlayer","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPlayerKeepAlive","c":"Player","p":{"instance":"BasePlayer","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnExcavatorMiningToggled","c":"Resource","p":{"instance":"ExcavatorArm"},"rb":0,"rt":null},{"n":"OnExcavatorResourceSet","c":"Resource","p":{"instance":"ExcavatorArm","text":"string","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnDieselEngineToggled","c":"Entity","p":{"instance":"DieselEngine"},"rb":0,"rt":null},{"n":"OnDieselEngineToggle","c":"Entity","p":{"instance":"DieselEngine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnActiveItemChanged","c":"Player","p":{"instance":"BasePlayer","activeItem":"Item","item":"Item"},"rb":0,"rt":null},{"n":"OnAmmoUnload","c":"Weapon","p":{"component":"BaseProjectile","item":"Item","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnOutputUpdate","c":"Electronic","p":{"instance":"IOEntity"},"rb":1,"rt":null},{"n":"OnInputUpdate","c":"Electronic","p":{"instance":"IOEntity","inputAmount":"int","inputSlot":"int"},"rb":1,"rt":null},{"n":"OnCardSwipe","c":"Electronic","p":{"instance":"CardReader","keycard":"Keycard","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnDigitalClockRing","c":"Electronic","p":{"instance":"DigitalClock"},"rb":1,"rt":null},{"n":"OnDigitalClockRingStop","c":"Electronic","p":{"instance":"DigitalClock"},"rb":1,"rt":null},{"n":"OnDigitalClockAlarmsSet","c":"Electronic","p":{"instance":"DigitalClock","digitalClockMessage":"ProtoBuf.DigitalClockMessage"},"rb":1,"rt":null},{"n":"OnButtonPress","c":"Electronic","p":{"instance":"PressButton","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnMapMarkersClear","c":"Player","p":{"instance":"BasePlayer","pointsOfInterest":"List<ProtoBuf.MapNote>"},"rb":1,"rt":null},{"n":"OnMapMarkersCleared","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnWireConnect","c":"Player","p":{"player":"BasePlayer","iOEntity":"IOEntity","inputIndex":"int","iOEntity2":"IOEntity","outputIndex":"int","linePoints":"List<UnityEngine.Vector3>","list`1":"List<float>"},"rb":1,"rt":null},{"n":"OnWireClear","c":"Player","p":{"ply":"BasePlayer","iOEntity":"IOEntity","clearIndex":"int","iOEntity2":"IOEntity","isInput":"bool"},"rb":1,"rt":null},{"n":"CanUseWires","c":"Player","p":{"player":"BasePlayer","cached":"bool","cacheDuration":"float"},"rb":1,"rt":"bool"},{"n":"OnHorseLead","c":"Animal","p":{"instance":"RidableHorse","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPhoneNameUpdate","c":"Electronic","p":{"instance":"PhoneController","newName":"string","currentPlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnBuildingPrivilege","c":"Entity","p":{"instance":"BaseEntity","obb":"OBB","cached":"bool","cacheDuration":"float","exclude":"BuildingPrivlidge"},"rb":1,"rt":"BuildingPrivlidge"},{"n":"OnHelicopterRetire","c":"Vehicle","p":{"instance":"PatrolHelicopterAI"},"rb":1,"rt":null},{"n":"OnCupboardProtectionCalculated","c":"Structure","p":{"instance":"BuildingPrivlidge","cachedProtectedMinutes":"float"},"rb":0,"rt":null},{"n":"OnCargoShipEgress","c":"Entity","p":{"instance":"CargoShip"},"rb":1,"rt":null},{"n":"OnCargoShipSpawnCrate","c":"Entity","p":{"instance":"CargoShip"},"rb":1,"rt":null},{"n":"OnNpcRadioChatter","c":"NPC","p":{"instance":"ScientistNPC"},"rb":1,"rt":null},{"n":"OnNpcAlert","c":"NPC","p":{"instance":"ScientistNPC"},"rb":1,"rt":null},{"n":"OnNpcEquipWeapon","c":"NPC","p":{"instance":"NPCPlayer","slot":"Item"},"rb":1,"rt":null},{"n":"OnNpcDuck","c":"NPC","p":{"instance":"HumanNPC"},"rb":1,"rt":null},{"n":"OnPlayerWantsDismount","c":"Player","p":{"player":"BasePlayer","instance":"BaseMountable"},"rb":1,"rt":null},{"n":"OnPlayerWantsMount","c":"Player","p":{"player":"BasePlayer","instance":"BaseMountable"},"rb":1,"rt":null},{"n":"OnPlayerStudyBlueprint","c":"Player","p":{"player":"BasePlayer","item":"Item"},"rb":1,"rt":null},{"n":"IOnPlayerConnected","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnBuildingSplit","c":"Structure","p":{"oldBuilding":"BuildingManager.Building","num":"uint"},"rb":0,"rt":null},{"n":"OnMapMarkerRemove","c":"Player","p":{"instance":"BasePlayer","pointsOfInterest":"List<ProtoBuf.MapNote>","num":"int"},"rb":1,"rt":null},{"n":"OnGrowableGathered","c":"Resource","p":{"instance":"GrowableEntity","item":"Item","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnQuarryConsumeFuel","c":"Resource","p":{"instance":"MiningQuarry","item":"Item"},"rb":3,"rt":"Item"},{"n":"OnEntityActiveCheck","c":"Entity","p":{"ent":"BaseEntity","player":"BasePlayer","id":"uint","debugName":"string"},"rb":1,"rt":"bool"},{"n":"OnEntityFromOwnerCheck","c":"Entity","p":{"ent":"BaseEntity","player":"BasePlayer","id":"uint","debugName":"string"},"rb":1,"rt":"bool"},{"n":"OnEntityVisibilityCheck","c":"Entity","p":{"ent":"BaseEntity","player":"BasePlayer","id":"uint","debugName":"string","maximumDistance":"float"},"rb":1,"rt":"bool"},{"n":"OnEntityDistanceCheck","c":"Entity","p":{"ent":"BaseEntity","player":"BasePlayer","id":"uint","debugName":"string","maximumDistance":"float","checkParent":"bool"},"rb":1,"rt":"bool"},{"n":"OnGrowableGather","c":"Resource","p":{"instance":"GrowableEntity","player":"BasePlayer","eat":"bool"},"rb":1,"rt":null},{"n":"OnItemSkinChange","c":"Item","p":{"inventoryId":"int","slot":"Item","instance":"RepairBench","basePlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnMapMarkerAdded","c":"Player","p":{"instance":"BasePlayer","mapNote":"ProtoBuf.MapNote"},"rb":0,"rt":null},{"n":"OnClothingItemChanged","c":"Player","p":{"instance":"PlayerInventory","item":"Item","bAdded":"bool"},"rb":0,"rt":null},{"n":"OnHorseHitch","c":"Animal","p":{"hitchable":"HitchTrough.IHitchable","spot":"HitchTrough.HitchSpot"},"rb":1,"rt":"bool"},{"n":"OnHorseUnhitch","c":"Animal","p":{"hitchable":"HitchTrough.IHitchable","hitchSpot":"HitchTrough.HitchSpot"},"rb":1,"rt":null},{"n":"OnVehiclePush","c":"Vehicle","p":{"instance":"BaseVehicle","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnEngineReverse","c":"Naval","p":{"instance":"SmallEngine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanRotateSail","c":"Naval","p":{"instance":"Sail","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"CanRaiseSail","c":"Naval","p":{"instance":"Sail","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"CanLowerSail","c":"Naval","p":{"instance":"Sail","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnBoatGroupSpawn","c":"Naval","p":{"instance":"BoatGroupSpawner","vector2":"UnityEngine.Vector2","quaternion":"UnityEngine.Quaternion","list":"HashSet<RHIB>","flag":"bool","spawnsPT":"bool"},"rb":1,"rt":null},{"n":"OnDeepSeaTeleport","c":"Naval","p":{"instance":"TriggerDeepSeaPortal","ent":"BaseEntity"},"rb":1,"rt":null},{"n":"OnBallistaGunReload","c":"Primitive","p":{"instance":"BallistaGun","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"CanLightCannonFuse","c":"Naval","p":{"instance":"Cannon"},"rb":1,"rt":"bool"},{"n":"OnEngineStart","c":"Naval","p":{"instance":"SmallEngine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnEngineStop","c":"Naval","p":{"instance":"SmallEngine","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPlayerBoatCollide","c":"Naval","p":{"instance":"PlayerBoat","baseEntity":"BaseEntity","collision":"UnityEngine.Collision"},"rb":1,"rt":null},{"n":"OnDeepSeaClosed","c":"Naval","p":{"deepSeaManager":"DeepSeaManager"},"rb":0,"rt":null},{"n":"OnDeepSeaOpened","c":"Naval","p":{"deepSeaManager":"DeepSeaManager"},"rb":0,"rt":null},{"n":"CanRagdollDismount","c":"Player","p":{"instance":"BaseRagdoll","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnCatapultFireForce","c":"Primitive","p":{"instance":"Catapult","shooter":"BasePlayer","num2":"float"},"rb":3,"rt":"float"},{"n":"OnStashHidden","c":"Entity","p":{"instance":"StashContainer","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMixingTableToggle","c":"Entity","p":{"instance":"MixingTable","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnBookmarkControl","c":"Bookmark","p":{"instance":"ComputerStation","player":"BasePlayer","text":"string","remoteControllable":"IRemoteControllable"},"rb":1,"rt":null},{"n":"OnBookmarkAdd","c":"Bookmark","p":{"instance":"ComputerStation","player":"BasePlayer","text":"string"},"rb":1,"rt":null},{"n":"OnBookmarksSendControl","c":"Bookmark","p":{"instance":"ComputerStation","player":"BasePlayer","text":"string"},"rb":1,"rt":null},{"n":"OnBookmarkControlEnd","c":"Bookmark","p":{"instance":"ComputerStation","ply":"BasePlayer","baseEntity":"BaseEntity"},"rb":1,"rt":null},{"n":"OnSolarPanelSunUpdate","c":"Electronic","p":{"instance":"SolarPanel","num":"int"},"rb":1,"rt":null},{"n":"OnServerInitialize","c":"Server","p":{},"rb":0,"rt":null},{"n":"OnDefaultItemsReceive","c":"Player","p":{"instance":"PlayerInventory"},"rb":1,"rt":null},{"n":"OnDefaultItemsReceived","c":"Player","p":{"instance":"PlayerInventory"},"rb":0,"rt":null},{"n":"IOnPlayerBanned","c":"Player","p":{"connection":"Network.Connection","Status":"AuthResponse"},"rb":0,"rt":null},{"n":"OnBonusItemDrop","c":"Item","p":{"item":"Item","basePlayer":"BasePlayer","container":"ItemContainer"},"rb":1,"rt":null},{"n":"OnPlayerCorpseSpawned","c":"Player","p":{"instance":"BasePlayer","playerCorpse":"PlayerCorpse"},"rb":0,"rt":null},{"n":"OnItemRefill","c":"Item","p":{"item":"Item","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnRfListenerAdd","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":1,"rt":null},{"n":"OnRfListenerRemove","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":1,"rt":null},{"n":"OnRfBroadcasterAdd","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":1,"rt":null},{"n":"OnRfBroadcasterRemove","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":1,"rt":null},{"n":"OnRfBroadcasterAdded","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":0,"rt":null},{"n":"OnRfListenerRemoved","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":0,"rt":null},{"n":"OnRfListenerAdded","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":0,"rt":null},{"n":"OnRfBroadcasterRemoved","c":"Radio","p":{"obj":"IRFObject","frequency":"int"},"rb":0,"rt":null},{"n":"OnRfFrequencyChange","c":"Radio","p":{"instance":"RFBroadcaster","num":"int","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnRfFrequencyChanged","c":"Radio","p":{"instance":"RFBroadcaster","num":"int","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnBonusItemDropped","c":"Item","p":{"item":"Item","basePlayer":"BasePlayer","container":"ItemContainer"},"rb":0,"rt":null},{"n":"OnRemoveDying","c":"Resource","p":{"instance":"GrowableEntity","receiver":"BasePlayer"},"rb":1,"rt":null},{"n":"OnSleepingBagDestroyed","c":"Entity","p":{"sleepingBag":"SleepingBag","userID":"ulong"},"rb":0,"rt":null},{"n":"OnAnalysisComplete","c":"Entity","p":{"instance":"SurveyCrater","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnBookmarkInput","c":"Bookmark","p":{"instance":"ComputerStation","player":"BasePlayer","inputState":"InputState"},"rb":1,"rt":null},{"n":"IOnServerInitialized","c":"Server","p":{},"rb":0,"rt":null},{"n":"OnEntityControl","c":"Electronic","p":{"instance":"AutoTurret","playerID":"ulong"},"rb":1,"rt":"bool"},{"n":"OnTurretRotate","c":"Turret","p":{"instance":"AutoTurret","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnSwitchToggled","c":"Entity","p":{"instance":"ElectricSwitch","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnEngineStatsRefresh","c":"Vehicle","p":{"instance":"VehicleModuleEngine","engineStorage":"Rust.Modular.EngineStorage"},"rb":1,"rt":null},{"n":"OnEngineStatsRefreshed","c":"Vehicle","p":{"instance":"VehicleModuleEngine","engineStorage":"Rust.Modular.EngineStorage"},"rb":0,"rt":null},{"n":"OnVehicleModulesAssign","c":"Vehicle","p":{"instance":"ModularCar","socketItemDefs":"Rust.Modular.ItemModVehicleModule[]"},"rb":1,"rt":null},{"n":"OnVehicleModulesAssigned","c":"Vehicle","p":{"instance":"ModularCar","socketItemDefs":"Rust.Modular.ItemModVehicleModule[]"},"rb":0,"rt":null},{"n":"OnVehicleModuleSelect","c":"Vehicle","p":{"vehicleItem":"Item","instance":"ModularCarGarage","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnVehicleModuleSelected","c":"Vehicle","p":{"vehicleItem":"Item","instance":"ModularCarGarage","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnVehicleModuleDeselected","c":"Vehicle","p":{"instance":"ModularCarGarage","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnNpcConversationRespond","c":"NPC","p":{"instance":"NPCTalking","player":"BasePlayer","conversationFor":"ConversationData","responseNode":"ConversationData.ResponseNode"},"rb":1,"rt":null},{"n":"OnNpcConversationResponded","c":"NPC","p":{"instance":"NPCTalking","player":"BasePlayer","conversationFor":"ConversationData","responseNode":"ConversationData.ResponseNode"},"rb":0,"rt":null},{"n":"OnVehicleLockableCheck","c":"Vehicle","p":{"instance":"ModularCarCodeLock"},"rb":4,"rt":"bool"},{"n":"OnElevatorCall","c":"Elevator","p":{"instance":"Elevator","elevatorEnt":"Elevator"},"rb":1,"rt":null},{"n":"OnHotAirBalloonToggle","c":"Entity","p":{"instance":"HotAirBalloon","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnHotAirBalloonToggled","c":"Entity","p":{"instance":"HotAirBalloon","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnEngineLoadoutRefresh","c":"Vehicle","p":{"instance":"Rust.Modular.EngineStorage"},"rb":1,"rt":null},{"n":"OnReactiveTargetReset","c":"Entity","p":{"instance":"ReactiveTarget"},"rb":0,"rt":null},{"n":"OnExperimentStarted","c":"Player","p":{"instance":"Workbench","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnExperimentEnd","c":"Player","p":{"instance":"Workbench"},"rb":4,"rt":null},{"n":"OnExperimentEnded","c":"Player","p":{"instance":"Workbench"},"rb":0,"rt":null},{"n":"OnVehicleModuleMove","c":"Vehicle","p":{"moduleForItem":"BaseVehicleModule","instance":"BaseModularVehicle","player":"BasePlayer"},"rb":4,"rt":"PlayerInventory.CanMoveFromResponse"},{"n":"CanSwapToSeat","c":"Player","p":{"player":"BasePlayer","instance":"BaseMountable"},"rb":1,"rt":"bool"},{"n":"OnRidableAnimalClaim","c":"Animal","p":{"instance":"RidableHorse","player":"BasePlayer","purchaseToken":"Item"},"rb":1,"rt":null},{"n":"OnRidableAnimalClaimed","c":"Animal","p":{"instance":"RidableHorse","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPhoneNameUpdated","c":"Electronic","p":{"instance":"PhoneController","PhoneName":"string","currentPlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"IOnEntitySaved","c":"Entity","p":{"instance":"BaseNetworkable","saveInfo":"BaseNetworkable.SaveInfo"},"rb":0,"rt":null},{"n":"OnEntitySnapshot","c":"Entity","p":{"instance":"BaseNetworkable","connection":"Network.Connection"},"rb":1,"rt":null},{"n":"OnNetworkGroupEntered","c":"Network","p":{"instance":"BaseNetworkable","group":"Network.Visibility.Group"},"rb":0,"rt":null},{"n":"OnNetworkGroupLeft","c":"Network","p":{"instance":"BaseNetworkable","group":"Network.Visibility.Group"},"rb":0,"rt":null},{"n":"OnDemoRecordingStart","c":"Player","p":{"text":"string","instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnDemoRecordingStarted","c":"Player","p":{"text":"string","instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnDemoRecordingStop","c":"Player","p":{"RecordFilename":"string","instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnDemoRecordingStopped","c":"Player","p":{"RecordFilename":"string","instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnOvenCook","c":"Entity","p":{"instance":"BaseOven","item":"Item"},"rb":4,"rt":null},{"n":"OnOvenCooked","c":"Entity","p":{"instance":"BaseOven","item":"Item","slot":"BaseEntity"},"rb":0,"rt":null},{"n":"OnFuelConsumed","c":"Fuel","p":{"instance":"BaseOven","fuel":"Item","burnable":"ItemModBurnable"},"rb":0,"rt":null},{"n":"OnFuelAmountCheck","c":"Fuel","p":{"instance":"EntityFuelSystem","fuelItem":"Item"},"rb":1,"rt":"int"},{"n":"OnFuelItemCheck","c":"Fuel","p":{"instance":"EntityFuelSystem","fuelContainer":"StorageContainer"},"rb":1,"rt":"Item"},{"n":"OnFuelCheck","c":"Fuel","p":{"instance":"EntityFuelSystem"},"rb":1,"rt":"bool"},{"n":"CanCheckFuel","c":"Fuel","p":{"instance":"EntityFuelSystem","fuelContainer":"StorageContainer","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"CanUseFuel","c":"Fuel","p":{"instance":"EntityFuelSystem","fuelContainer":"StorageContainer","seconds":"float","fuelUsedPerSecond":"float"},"rb":4,"rt":"bool"},{"n":"OnIORefCleared","c":"Electronic","p":{"instance":"IOEntity.IORef","obj":"IOEntity"},"rb":0,"rt":null},{"n":"OnTechTreeNodeUnlock","c":"TechTree","p":{"instance":"Workbench","byID":"TechTreeData.NodeInstance","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTechTreeNodeUnlocked","c":"TechTree","p":{"instance":"Workbench","byID":"TechTreeData.NodeInstance","player":"BasePlayer","pooledList`1":"PooledList<ItemDefinition>"},"rb":0,"rt":null},{"n":"OnPhoneAnswer","c":"Phone","p":{"instance":"PhoneController","activeCallTo":"PhoneController"},"rb":4,"rt":null},{"n":"OnPhoneCallStart","c":"Phone","p":{"instance":"PhoneController","activeCallTo":"PhoneController","currentPlayer":"BasePlayer"},"rb":4,"rt":null},{"n":"OnPhoneCallStarted","c":"Phone","p":{"instance":"PhoneController","activeCallTo":"PhoneController","currentPlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"CanReceiveCall","c":"Phone","p":{"instance":"PhoneController"},"rb":1,"rt":"bool"},{"n":"OnPhoneDial","c":"Phone","p":{"instance":"PhoneController","telephone":"PhoneController","currentPlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPhoneDialFail","c":"Phone","p":{"instance":"PhoneController","reason":"Telephone.DialFailReason","currentPlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPhoneDialTimeout","c":"Phone","p":{"activeCallTo":"PhoneController","instance":"PhoneController","currentPlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPhoneDialFailed","c":"Phone","p":{"instance":"PhoneController","reason":"Telephone.DialFailReason","currentPlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPhoneDialTimedOut","c":"Phone","p":{"activeCallTo":"PhoneController","instance":"PhoneController","currentPlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPhoneAnswered","c":"Phone","p":{"instance":"PhoneController","activeCallTo":"PhoneController"},"rb":0,"rt":null},{"n":"OnInventoryNetworkUpdate","c":"Player","p":{"instance":"PlayerInventory","container":"ItemContainer","updateItemContainer":"ProtoBuf.UpdateItemContainer","type":"PlayerInventory.Type","mode":"PlayerInventory.NetworkInventoryMode"},"rb":1,"rt":null},{"n":"OnEntityFlagsNetworkUpdate","c":"Entity","p":{"instance":"BaseEntity"},"rb":1,"rt":null},{"n":"OnLootNetworkUpdate","c":"Player","p":{"instance":"PlayerLoot"},"rb":1,"rt":null},{"n":"OnVendingTransaction","c":"Vending","p":{"instance":"VendingMachine","buyer":"BasePlayer","sellOrderId":"int","numberOfTransactions":"int","targetContainer":"ItemContainer"},"rb":1,"rt":"bool"},{"n":"OnFindSpawnPoint","c":"Player","p":{"forPlayer":"BasePlayer","teamId":"ulong"},"rb":1,"rt":"BasePlayer.SpawnPoint"},{"n":"OnBookmarkControlStarted","c":"Bookmark","p":{"instance":"ComputerStation","player":"BasePlayer","text":"string","remoteControllable":"IRemoteControllable"},"rb":0,"rt":null},{"n":"OnBookmarkControlEnded","c":"Bookmark","p":{"instance":"ComputerStation","ply":"BasePlayer","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnCargoPlaneSignaled","c":"Entity","p":{"baseEntity":"BaseEntity","instance":"SupplySignal"},"rb":0,"rt":null},{"n":"OnSupplyDropDropped","c":"Entity","p":{"baseEntity":"BaseEntity","instance":"CargoPlane"},"rb":0,"rt":null},{"n":"OnItemSubmit","c":"Item","p":{"slot":"Item","instance":"Mailbox","fromPlayer":"BasePlayer"},"rb":1,"rt":null},{"n":"OnItemStacked","c":"Item","p":{"item":"Item","instance":"Item","newcontainer":"ItemContainer","int32":"int"},"rb":0,"rt":null},{"n":"OnThreatLevelUpdate","c":"Player","p":{"instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnWaterPurify","c":"Entity","p":{"instance":"WaterPurifier","timeCooked":"float"},"rb":4,"rt":null},{"n":"OnWaterPurified","c":"Entity","p":{"instance":"WaterPurifier","timeCooked":"float"},"rb":0,"rt":null},{"n":"CanUseGesture","c":"Player","p":{"player":"BasePlayer","instance":"GestureConfig"},"rb":1,"rt":"bool"},{"n":"OnClientDisconnect","c":"Player","p":{"connection":"Network.Connection","text":"string"},"rb":0,"rt":null},{"n":"OnIngredientsCollect","c":"Crafting","p":{"instance":"ItemCrafter","bp":"ItemBlueprint","task":"ItemCraftTask","amount":"int","player":"BasePlayer","takeBroken":"bool"},"rb":1,"rt":null},{"n":"OnRespawnInformationGiven","c":"Player","p":{"instance":"BasePlayer","list`1":"List<ProtoBuf.RespawnInformation.SpawnOptions>"},"rb":0,"rt":null},{"n":"OnClientCommand","c":"Player","p":{"connection":"Network.Connection","text":"string"},"rb":1,"rt":null},{"n":"OnSleepingBagValidCheck","c":"Entity","p":{"instance":"SleepingBag","playerID":"ulong","ignoreTimers":"bool"},"rb":1,"rt":"bool"},{"n":"OnCupboardAuthorize","c":"Structure","p":{"buildingPrivlidge":"BuildingPrivlidge","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnWaterCollect","c":"Entity","p":{"instance":"WaterCatcher"},"rb":1,"rt":null},{"n":"OnLiquidVesselFill","c":"Entity","p":{"instance":"BaseLiquidVessel","ownerPlayer":"BasePlayer","facingLiquidContainer":"LiquidContainer"},"rb":1,"rt":null},{"n":"OnCCTVDirectionChange","c":"Electronic","p":{"instance":"CCTV_RC","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanSetRelationship","c":"Player","p":{"player":"BasePlayer","otherPlayer":"BasePlayer","type":"RelationshipManager.RelationshipType","weight":"int"},"rb":1,"rt":null},{"n":"OnPlayerRecover","c":"Player","p":{"instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPlayerWound","c":"Player","p":{"instance":"BasePlayer","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnPlayerRecovered","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerDismountFailed","c":"Player","p":{"player":"BasePlayer","instance":"BaseMountable"},"rb":0,"rt":null},{"n":"CanAccessVendingMachine","c":"Vending","p":{"config":"DeliveryDroneConfig","vendingMachine":"VendingMachine"},"rb":1,"rt":"bool"},{"n":"OnDecayHeal","c":"Entity","p":{"instance":"DecayEntity"},"rb":1,"rt":null},{"n":"OnDecayDamage","c":"Entity","p":{"instance":"DecayEntity"},"rb":1,"rt":null},{"n":"OnWindmillUpdate","c":"Entity","p":{"instance":"ElectricWindmill"},"rb":1,"rt":null},{"n":"OnWindmillUpdated","c":"Entity","p":{"instance":"ElectricWindmill"},"rb":0,"rt":null},{"n":"CanMannequinChangePose","c":"Entity","p":{"instance":"Mannequin","player":"BasePlayer"},"rb":4,"rt":null},{"n":"CanMannequinSwap","c":"Entity","p":{"instance":"Mannequin","player":"BasePlayer"},"rb":4,"rt":null},{"n":"CanElevatorLiftMove","c":"Elevator","p":{"instance":"ElevatorLift"},"rb":1,"rt":"bool"},{"n":"OnGrowableStateChange","c":"Resource","p":{"instance":"GrowableEntity","state":"PlantProperties.State"},"rb":1,"rt":null},{"n":"CanBeRecycled","c":"Crafting","p":{"item":"Item","instance":"Recycler"},"rb":1,"rt":"bool"},{"n":"OnEntityPickedUp","c":"Entity","p":{"instance":"BaseCombatEntity","createdItem":"Item","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnServerRestartInterrupt","c":"Server","p":{},"rb":1,"rt":null},{"n":"OnServerRestart","c":"Server","p":{"strNotice":"string","iSeconds":"int"},"rb":1,"rt":null},{"n":"CanUseHelicopter","c":"Vehicle","p":{"player":"BasePlayer","instance":"CH47HelicopterAIController"},"rb":1,"rt":null},{"n":"OnWildlifeTrap","c":"Traps","p":{"instance":"WildlifeTrap","trapped":"TrappableWildlife"},"rb":1,"rt":null},{"n":"OnFishingStopped","c":"Fishing","p":{"instance":"BaseFishingRod","reason":"BaseFishingRod.FailReason"},"rb":0,"rt":null},{"n":"OnFishingRodCast","c":"Fishing","p":{"instance":"BaseFishingRod","ownerPlayer":"BasePlayer","currentLure":"Item"},"rb":0,"rt":null},{"n":"OnTurretAuthorize","c":"Turret","p":{"instance":"AutoTurret","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnFishCaught","c":"Fishing","p":{"currentFishTarget":"ItemDefinition","instance":"BaseFishingRod","ownerPlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerDrink","c":"Player","p":{"player":"BasePlayer","instance":"LiquidContainer"},"rb":1,"rt":null},{"n":"CanPurchaseItem","c":"Vending","p":{"buyer":"BasePlayer","item":"Item","onItemPurchased":"Action<BasePlayer, Item>","instance":"VendingMachine","targetContainer":"ItemContainer"},"rb":4,"rt":"bool"},{"n":"OnTurretAssign","c":"Turret","p":{"instance":"AutoTurret","num":"ulong","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnTurretAssigned","c":"Turret","p":{"instance":"AutoTurret","num":"ulong","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMissionFailed","c":"Mission","p":{"instance":"BaseMission","instance2":"BaseMission.MissionInstance","assignee":"BasePlayer","failReason":"BaseMission.MissionFailReason"},"rb":0,"rt":null},{"n":"OnMissionSucceeded","c":"Mission","p":{"instance":"BaseMission","missionInstance":"BaseMission.MissionInstance","assignee":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMissionStart","c":"Mission","p":{"instance":"BaseMission","missionInstance":"BaseMission.MissionInstance","assignee":"BasePlayer"},"rb":1,"rt":null},{"n":"CanAssignMission","c":"Mission","p":{"assignee":"BasePlayer","mission":"BaseMission","provider":"IMissionProvider"},"rb":1,"rt":"bool"},{"n":"OnMissionAssigned","c":"Mission","p":{"mission":"BaseMission","provider":"IMissionProvider","assignee":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMissionStarted","c":"Mission","p":{"instance":"BaseMission","missionInstance":"BaseMission.MissionInstance","assignee":"BasePlayer"},"rb":0,"rt":null},{"n":"OnFlameExplosion","c":"Weapon","p":{"instance":"FlameExplosive","component":"UnityEngine.Collider"},"rb":0,"rt":null},{"n":"OnEyePosValidate","c":"Player","p":{"instance":"AttackEntity","player":"BasePlayer","eyePos":"UnityEngine.Vector3","checkLineOfSight":"bool"},"rb":1,"rt":"bool"},{"n":"OnImpactEffectCreate","c":"Weapon","p":{"info":"HitInfo","customEffect":"string"},"rb":4,"rt":null},{"n":"OnItemUnwrap","c":"Item","p":{"item":"Item","player":"BasePlayer","instance":"ItemModUnwrap"},"rb":4,"rt":null},{"n":"OnXmasLootDistribute","c":"Seasonal","p":{"instance":"XMasRefill"},"rb":1,"rt":null},{"n":"OnXmasStockingFill","c":"Seasonal","p":{"instance":"Stocking"},"rb":1,"rt":null},{"n":"OnBradleyApcThink","c":"Vehicle","p":{"instance":"BradleyAPC"},"rb":1,"rt":null},{"n":"OnTerrainCreate","c":"World","p":{"instance":"TerrainGenerator"},"rb":0,"rt":null},{"n":"OnPlayerColliderEnable","c":"Player","p":{"instance":"BasePlayer","playerCollider":"UnityEngine.CapsuleCollider"},"rb":1,"rt":null},{"n":"OnPlayerSleepEnded","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnExcavatorSuppliesRequest","c":"Electronic","p":{"instance":"ExcavatorSignalComputer","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnExcavatorSuppliesRequested","c":"Electronic","p":{"instance":"ExcavatorSignalComputer","player":"BasePlayer","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnClientProjectileEffectCreate","c":"Player","p":{"sourceConnection":"Network.Connection","instance":"BaseProjectile","prefabName":"string"},"rb":4,"rt":null},{"n":"CanDesignFirework","c":"Firework","p":{"player":"BasePlayer","instance":"PatternFirework"},"rb":1,"rt":"bool"},{"n":"OnFireworkStarted","c":"Firework","p":{"instance":"BaseFirework"},"rb":0,"rt":null},{"n":"OnFireworkExhausted","c":"Firework","p":{"instance":"BaseFirework"},"rb":0,"rt":null},{"n":"OnFireworkDamage","c":"Firework","p":{"instance":"BaseFirework","info":"HitInfo"},"rb":4,"rt":null},{"n":"OnFireworkDesignChange","c":"Firework","p":{"instance":"PatternFirework","design":"ProtoBuf.PatternFirework.Design","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnFireworkDesignChanged","c":"Firework","p":{"instance":"PatternFirework","design":"ProtoBuf.PatternFirework.Design","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnServerInformationUpdated","c":"Server","p":{},"rb":0,"rt":null},{"n":"OnMlrsFire","c":"Vehicle","p":{"instance":"MLRS","owner":"BasePlayer"},"rb":1,"rt":null},{"n":"OnMlrsFired","c":"Vehicle","p":{"instance":"MLRS","owner":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMlrsRocketFired","c":"Vehicle","p":{"instance":"MLRS","serverProjectile":"ServerProjectile"},"rb":0,"rt":null},{"n":"OnMlrsFiringEnded","c":"Vehicle","p":{"instance":"MLRS"},"rb":0,"rt":null},{"n":"OnMlrsTarget","c":"Vehicle","p":{"instance":"MLRS","worldPos":"UnityEngine.Vector3","_mounted":"BasePlayer"},"rb":1,"rt":null},{"n":"OnMlrsTargetSet","c":"Vehicle","p":{"instance":"MLRS","trueTargetHitPos":"UnityEngine.Vector3","_mounted":"BasePlayer"},"rb":0,"rt":null},{"n":"OnEntityReskin","c":"Crafting","p":{"baseEntity":"BaseEntity","uInt64":"ulong","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnEntityReskinned","c":"Crafting","p":{"baseEntity":"BaseEntity","uInt64":"ulong","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnConnectionDequeue","c":"Queue","p":{"connection":"Network.Connection"},"rb":1,"rt":null},{"n":"OnConnectionQueue","c":"Queue","p":{"connection":"Network.Connection"},"rb":1,"rt":null},{"n":"OnQueueUpdate","c":"Queue","p":{"c":"Network.Connection","position":"int"},"rb":1,"rt":null},{"n":"OnQueueCycle","c":"Queue","p":{"availableSlots":"int"},"rb":1,"rt":null},{"n":"OnSprinklerSplashed","c":"Entity","p":{"instance":"Sprinkler"},"rb":0,"rt":null},{"n":"CanWaterBallSplash","c":"Entity","p":{"liquidDef":"ItemDefinition","position":"UnityEngine.Vector3","radius":"float","amount":"int","funWater":"bool"},"rb":1,"rt":"bool"},{"n":"CanFireLiquidWeapon","c":"Weapon","p":{"player":"BasePlayer","instance":"LiquidWeapon"},"rb":1,"rt":"bool"},{"n":"OnLiquidWeaponFired","c":"Weapon","p":{"instance":"LiquidWeapon","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnLiquidWeaponFiringStopped","c":"Weapon","p":{"instance":"LiquidWeapon"},"rb":0,"rt":null},{"n":"OnPhotoCapture","c":"Entity","p":{"photoEntity":"PhotoEntity","item":"Item","player":"BasePlayer","array":"byte[]"},"rb":1,"rt":null},{"n":"OnPhotoCaptured","c":"Entity","p":{"photoEntity":"PhotoEntity","item":"Item","player":"BasePlayer","array":"byte[]"},"rb":0,"rt":null},{"n":"OnNpcTargetSense","c":"NPC","p":{"owner":"BaseEntity","ent":"BaseEntity","brainSenses":"AIBrainSenses"},"rb":1,"rt":null},{"n":"OnTreeMarkerHit","c":"Entity","p":{"instance":"TreeEntity","info":"HitInfo"},"rb":1,"rt":"bool"},{"n":"OnNetworkSubscriptionsGather","c":"Network","p":{"instance":"NetworkVisibilityGrid","group":"Network.Visibility.Group","groups":"ListHashSet<Network.Visibility.Group>","radius":"int"},"rb":1,"rt":null},{"n":"OnVendingShopOpened","c":"Vending","p":{"instance":"VendingMachine","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnAdventGiftAward","c":"Seasonal","p":{"instance":"AdventCalendar","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnAdventGiftAwarded","c":"Seasonal","p":{"instance":"AdventCalendar","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnItemPainted","c":"Item","p":{"instance":"PaintedItemStorageEntity","item":"Item","player":"BasePlayer","array":"byte[]"},"rb":0,"rt":null},{"n":"OnItemRecycleAmount","c":"Item","p":{"slot":"Item","num3":"int","instance":"Recycler"},"rb":3,"rt":"int"},{"n":"OnTrainCarUncouple","c":"Vehicle","p":{"instance":"TrainCar","player":"BasePlayer"},"rb":1,"rt":null},{"n":"CanTrainCarCouple","c":"Vehicle","p":{"owner":"TrainCar","owner2":"TrainCar"},"rb":1,"rt":"bool"},{"n":"OnSamSiteModeToggle","c":"Entity","p":{"instance":"SamSite","player":"BasePlayer","flag":"bool"},"rb":1,"rt":null},{"n":"OnSprayCreate","c":"Crafting","p":{"instance":"SprayCan","vector3":"UnityEngine.Vector3","quaternion":"UnityEngine.Quaternion"},"rb":1,"rt":null},{"n":"OnLockerSwap","c":"Structure","p":{"instance":"Locker","num":"int","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnWeaponModChange","c":"Weapon","p":{"instance":"BaseProjectile","GetOwnerPlayer()":"BasePlayer"},"rb":1,"rt":null},{"n":"OnSprayRemove","c":"Entity","p":{"instance":"SprayCanSpray","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnComposterUpdate","c":"Entity","p":{"instance":"Composter"},"rb":1,"rt":null},{"n":"OnInventoryItemsCount","c":"Item","p":{"instance":"PlayerInventory","itemid":"int","includeBackpack":"bool"},"rb":1,"rt":"int"},{"n":"OnInventoryItemsTake","c":"Item","p":{"instance":"PlayerInventory","collect":"List<Item>","itemid":"int","amount":"int"},"rb":1,"rt":"int"},{"n":"OnInventoryItemsFind","c":"Item","p":{"instance":"PlayerInventory","id":"int","list":"List<Item>"},"rb":4,"rt":null},{"n":"OnInventoryAmmoFind","c":"Item","p":{"instance":"PlayerInventory","list":"List<Item>","ammoType":"Rust.AmmoTypes"},"rb":1,"rt":null},{"n":"OnBackpackDrop","c":"Item","p":{"anyBackpack":"Item","instance":"PlayerInventory"},"rb":1,"rt":null},{"n":"OnDroppedItemCombined","c":"Item","p":{"instance":"DroppedItem"},"rb":0,"rt":null},{"n":"OnRemoteIdentifierUpdate","c":"Entity","p":{"instance":"PoweredRemoteControlEntity","newID":"string"},"rb":1,"rt":null},{"n":"OnOvenStart","c":"Entity","p":{"instance":"BaseOven"},"rb":1,"rt":null},{"n":"OnOvenStarted","c":"Entity","p":{"instance":"BaseOven"},"rb":0,"rt":null},{"n":"OnPlayerBanned","c":"Player","p":{"connection":"Network.Connection","ToString()":"string"},"rb":0,"rt":null},{"n":"OnOvenTemperature","c":"Entity","p":{"instance":"BaseOven","slot":"int"},"rb":1,"rt":"float"},{"n":"OnBuildingMerge","c":"Structure","p":{"instance":"ServerBuildingManager","building1":"BuildingManager.Building","building2":"BuildingManager.Building"},"rb":0,"rt":null},{"n":"OnPortalUse","c":"Player","p":{"player":"BasePlayer","instance":"BasePortal"},"rb":4,"rt":null},{"n":"OnPortalUsed","c":"Player","p":{"player":"BasePlayer","instance":"BasePortal"},"rb":0,"rt":null},{"n":"OnItemDespawn","c":"Item","p":{"item":"Item"},"rb":0,"rt":null},{"n":"OnVehicleLockRequest","c":"Vehicle","p":{"instance":"ModularCarGarage","player":"BasePlayer","text":"string"},"rb":1,"rt":null},{"n":"OnConveyorFiltersChange","c":"Industrial","p":{"instance":"IndustrialConveyor","player":"BasePlayer","itemFilterList":"ProtoBuf.IndustrialConveyor.ItemFilterList"},"rb":4,"rt":null},{"n":"OnVehicleHornPressed","c":"Vehicle","p":{"instance":"VehicleModuleSeating","player":"BasePlayer"},"rb":0,"rt":null},{"n":"CanExplosiveStick","c":"Entity","p":{"instance":"TimedExplosive","entity":"BaseEntity"},"rb":1,"rt":"bool"},{"n":"OnInventoryAmmoItemFind","c":"Item","p":{"inventory":"PlayerInventory","fuelType":"ItemDefinition"},"rb":1,"rt":"Item"},{"n":"OnStashOcclude","c":"Entity","p":{"instance":"StashContainer"},"rb":1,"rt":null},{"n":"OnBedMade","c":"Entity","p":{"instance":"SleepingBag","player":"BasePlayer"},"rb":0,"rt":null},{"n":"IOnPlayerChat","c":"Player","p":{"userId":"ulong","username":"string","<strChatText>5__2":"string","targetChannel":"ConVar.Chat.ChatChannel","player":"BasePlayer"},"rb":3,"rt":"bool"},{"n":"OnCodeChanged","c":"Structure","p":{"player":"BasePlayer","instance":"CodeLock","text":"string","flag":"bool"},"rb":0,"rt":null},{"n":"OnMagazineReload","c":"Weapon","p":{"instance":"BaseProjectile","ammoSource":"IAmmoContainer","GetOwnerPlayer()":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnRackedWeaponMount","c":"Item","p":{"item":"Item","player":"BasePlayer","instance":"WeaponRack"},"rb":4,"rt":"bool"},{"n":"OnRackedWeaponMounted","c":"Item","p":{"item":"Item","player":"BasePlayer","instance":"WeaponRack"},"rb":0,"rt":null},{"n":"OnRackedWeaponSwap","c":"Item","p":{"item":"Item","weaponAtIndex":"WeaponRackSlot","player":"BasePlayer","instance":"WeaponRack"},"rb":1,"rt":null},{"n":"OnRackedWeaponSwapped","c":"Item","p":{"item":"Item","weaponAtIndex":"WeaponRackSlot","player":"BasePlayer","instance":"WeaponRack"},"rb":0,"rt":null},{"n":"OnRackedWeaponTake","c":"Item","p":{"slot":"Item","player":"BasePlayer","instance":"WeaponRack"},"rb":1,"rt":null},{"n":"OnRackedWeaponTaken","c":"Item","p":{"slot":"Item","player":"BasePlayer","instance":"WeaponRack"},"rb":0,"rt":null},{"n":"OnRackedWeaponUnload","c":"Item","p":{"slot":"Item","player":"BasePlayer","instance":"WeaponRack"},"rb":1,"rt":null},{"n":"OnRackedWeaponUnloaded","c":"Item","p":{"slot":"Item","player":"BasePlayer","instance":"WeaponRack"},"rb":0,"rt":null},{"n":"OnRackedWeaponLoad","c":"Item","p":{"slot":"Item","itemDefinition":"ItemDefinition","player":"BasePlayer","instance":"WeaponRack"},"rb":1,"rt":null},{"n":"OnRackedWeaponLoaded","c":"Item","p":{"slot":"Item","itemDefinition":"ItemDefinition","player":"BasePlayer","instance":"WeaponRack"},"rb":0,"rt":null},{"n":"OnEntityLoaded","c":"Entity","p":{"instance":"BaseNetworkable","info":"BaseNetworkable.LoadInfo"},"rb":0,"rt":null},{"n":"OnTimedExplosiveExplode","c":"Weapon","p":{"instance":"TimedExplosive","explosionFxPos":"UnityEngine.Vector3"},"rb":1,"rt":null},{"n":"CanFastTrackCraftTask","c":"Item","p":{"instance":"ItemCrafter","itemCraftTask":"ItemCraftTask","taskID":"int"},"rb":1,"rt":"bool"},{"n":"CanBeHomingTargeted","c":"Weapon","p":{"instance":"BaseHelicopter"},"rb":1,"rt":"bool"},{"n":"OnInterferenceUpdate","c":"Turret","p":{"instance":"AutoTurret"},"rb":1,"rt":null},{"n":"OnEventCollectablePickup","c":"Seasonal","p":{"player":"BasePlayer","instance":"CollectableEasterEgg"},"rb":4,"rt":null},{"n":"OnHuntEventStart","c":"Seasonal","p":{"instance":"EggHuntEvent"},"rb":4,"rt":null},{"n":"OnHuntEventEnd","c":"Seasonal","p":{"instance":"EggHuntEvent"},"rb":4,"rt":null},{"n":"OnPatrolHelicopterTakeDamage","c":"Entity","p":{"instance":"PatrolHelicopter","info":"HitInfo"},"rb":1,"rt":null},{"n":"CanLockerAcceptItem","c":"Item","p":{"instance":"Locker","item":"Item","targetSlot":"int"},"rb":1,"rt":"bool"},{"n":"OnCoalingTowerStart","c":"Resource","p":{"instance":"CoalingTower","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPatrolHelicopterKill","c":"Entity","p":{"instance":"PatrolHelicopter","info":"HitInfo"},"rb":1,"rt":null},{"n":"OnPlayerDig","c":"Player","p":{"player":"BasePlayer","instance":"BaseDiggableEntity"},"rb":1,"rt":null},{"n":"OnPoweredLightsPointAdd","c":"Structure","p":{"instance":"StringLights","player":"BasePlayer","vector":"UnityEngine.Vector3","vector2":"UnityEngine.Vector3"},"rb":1,"rt":null},{"n":"OnSignContentCopied","c":"Structure","p":{"instance":"SignContent","s":"ISignage","b":"IUGCBrowserEntity"},"rb":0,"rt":null},{"n":"OnQuarryToggle","c":"Resource","p":{"miningQuarry":"MiningQuarry","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnQuarryToggled","c":"Resource","p":{"miningQuarry":"MiningQuarry","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnNpcConversationEnded","c":"NPC","p":{"instance":"NPCTalking","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnNpcConversationStart","c":"NPC","p":{"instance":"NPCTalking","ply":"BasePlayer","conversationData":"ConversationData"},"rb":1,"rt":null},{"n":"OnDispenserGathered","c":"Resource","p":{"instance":"ResourceDispenser","entity":"BasePlayer","item":"Item"},"rb":0,"rt":null},{"n":"OnCollectiblePickedup","c":"Resource","p":{"instance":"CollectibleEntity","reciever":"BasePlayer","item":"Item"},"rb":0,"rt":null},{"n":"OnDispenserBonusReceived","c":"Resource","p":{"instance":"ResourceDispenser","player":"BasePlayer","item2":"Item"},"rb":0,"rt":null},{"n":"OnItemFilter","c":"Item","p":{"item":"Item","instance":"StorageContainer","targetSlot":"int"},"rb":1,"rt":"bool"},{"n":"OnPlanterBoxFertilize","c":"Entity","p":{"instance":"PlanterBox"},"rb":1,"rt":null},{"n":"OnPlayerMarkersSend","c":"Player","p":{"instance":"BasePlayer","mapNoteList":"ProtoBuf.MapNoteList"},"rb":0,"rt":null},{"n":"OnPlayerPingsSend","c":"Player","p":{"instance":"BasePlayer","mapNoteList":"ProtoBuf.MapNoteList"},"rb":0,"rt":null},{"n":"OnBoomboxStationValidate","c":"Radio","p":{"url":"string"},"rb":1,"rt":"bool"},{"n":"OnBoomboxToggle","c":"Radio","p":{"instance":"BoomBox","player":"BasePlayer","flag":"bool"},"rb":4,"rt":null},{"n":"OnBoomboxStationUpdate","c":"Radio","p":{"instance":"BoomBox","text":"string","player":"BasePlayer"},"rb":4,"rt":null},{"n":"OnBoomboxStationUpdated","c":"Radio","p":{"instance":"BoomBox","text":"string","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnAIBrainStateSwitch","c":"NPC","p":{"instance":"BaseAIBrain","CurrentState":"BaseAIBrain.BasicAIState","newState":"BaseAIBrain.BasicAIState"},"rb":4,"rt":"bool"},{"n":"OnAIBrainStateSwitched","c":"NPC","p":{"instance":"BaseAIBrain","CurrentState":"BaseAIBrain.BasicAIState"},"rb":0,"rt":null},{"n":"OnCrateLaptopAttack","c":"Entity","p":{"instance":"HackableLockedCrate","info":"HitInfo"},"rb":1,"rt":null},{"n":"CanSeeStash","c":"Entity","p":{"instance":"BasePlayer","Entity":"StashContainer"},"rb":1,"rt":null},{"n":"OnStashExposed","c":"Entity","p":{"Entity":"StashContainer","instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnMetalDetectorFlagRequest","c":"Player","p":{"instance":"BaseMetalDetector","vector":"UnityEngine.Vector3","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnCargoShipHarborApproach","c":"Entity","p":{"instance":"CargoShip","cn":"CargoNotifier"},"rb":1,"rt":null},{"n":"OnCargoShipHarborArrived","c":"Entity","p":{"instance":"CargoShip"},"rb":0,"rt":null},{"n":"OnCargoShipHarborLeave","c":"Entity","p":{"instance":"CargoShip"},"rb":0,"rt":null},{"n":"OnNoGoZoneAdded","c":"Entity","p":{"instance":"PatrolHelicopterAI","zone":"PatrolHelicopterAI.DangerZone"},"rb":4,"rt":null},{"n":"CanUseHBHFSensor","c":"Player","p":{"player":"BasePlayer","instance":"HBHFSensor"},"rb":1,"rt":"bool"},{"n":"OnElevatorButtonPress","c":"Elevator","p":{"instance":"ElevatorLift","player":"BasePlayer","num":"int","flag":"bool"},"rb":4,"rt":null},{"n":"OnEventTrigger","c":"Entity","p":{"instance":"TriggeredEventPrefab"},"rb":1,"rt":null},{"n":"OnPlayerPveDamage","c":"Structure","p":{"Initiator":"BaseEntity","info":"HitInfo","instance":"BuildingBlock"},"rb":4,"rt":null},{"n":"IOnCupboardAuthorize","c":"Structure","p":{"num":"ulong","player":"BasePlayer","instance":"BuildingPrivlidge"},"rb":4,"rt":null},{"n":"OnStructureUpgraded","c":"Structure","p":{"instance":"BuildingBlock","player":"BasePlayer","type":"BuildingGrade.Enum","skin":"ulong"},"rb":0,"rt":null},{"n":"CanDeployScientists","c":"NPC","p":{"instance":"BradleyAPC","attacker":"BaseEntity","scientistPrefabs":"List<GameObjectRef>","spawnPositions":"List<UnityEngine.Vector3>"},"rb":1,"rt":"bool"},{"n":"OnScientistInitialized","c":"NPC","p":{"instance":"BradleyAPC","scientist":"ScientistNPC","spawnPos":"UnityEngine.Vector3"},"rb":0,"rt":null},{"n":"OnScientistRecalled","c":"NPC","p":{"instance":"BradleyAPC","scientist":"ScientistNPC"},"rb":0,"rt":null},{"n":"OnLockRemove","c":"Vehicle","p":{"carOccupant":"ModularCar","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnCodeChange","c":"Vehicle","p":{"carOccupant":"ModularCar","player":"BasePlayer","text":"string"},"rb":1,"rt":null},{"n":"CanDestroyLock","c":"Vehicle","p":{"player":"BasePlayer","instance":"ModularCar","viaModule":"BaseVehicleModule"},"rb":1,"rt":"bool"},{"n":"OnDebrisSpawn","c":"Entity","p":{"instance":"DecayEntity","localPos":"UnityEngine.Vector3","rot":"UnityEngine.Quaternion","dropToTerrain":"bool"},"rb":1,"rt":null},{"n":"OnCorpsePopulate","c":"NPC","p":{"instance":"NPCPlayer","nPCPlayerCorpse":"NPCPlayerCorpse"},"rb":1,"rt":"BaseCorpse"},{"n":"OnInventoryItemFind","c":"Item","p":{"instance":"PlayerInventory","id":"int"},"rb":1,"rt":"Item"},{"n":"OnPlayerHandcuff","c":"Player","p":{"victim":"BasePlayer","handcuffer":"BasePlayer"},"rb":4,"rt":null},{"n":"OnPlayerHandcuffed","c":"Player","p":{"victim":"BasePlayer","handcuffer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerVanish","c":"Player","p":{"basePlayer":"BasePlayer"},"rb":4,"rt":null},{"n":"OnPlayerVanished","c":"Player","p":{"basePlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerDropActiveItem","c":"Player","p":{"player":"BasePlayer","item":"Item"},"rb":1,"rt":null},{"n":"OnFrankensteinPetWake","c":"Pet","p":{"instance":"FrankensteinTable","owner":"BasePlayer"},"rb":4,"rt":null},{"n":"OnFrankensteinPetSleep","c":"Pet","p":{"frankensteinPet":"FrankensteinPet","instance":"FrankensteinTable","owner":"BasePlayer"},"rb":4,"rt":null},{"n":"OnFreeableContainerRelease","c":"Entity","p":{"instance":"FreeableLootContainer","ply":"BasePlayer"},"rb":1,"rt":null},{"n":"OnFreeableContainerReleased","c":"Entity","p":{"instance":"FreeableLootContainer","ply":"BasePlayer"},"rb":0,"rt":null},{"n":"OnFreeableContainerReleaseStarted","c":"Entity","p":{"instance":"FreeableLootContainer","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnSiegeWeaponFire","c":"Primitive","p":{"instance":"Catapult","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnAnimalDungProduce","c":"Animal","p":{"instance":"RidableHorse"},"rb":4,"rt":null},{"n":"OnAnimalDungProduced","c":"Animal","p":{"instance":"RidableHorse","item":"Item"},"rb":0,"rt":null},{"n":"OnActiveTelephoneUpdated","c":"Player","p":{"instance":"BasePlayer","t":"PhoneController"},"rb":0,"rt":null},{"n":"OnSiegeWeaponDoorOpen","c":"Primitive","p":{"instance":"BatteringRam","player":"BasePlayer"},"rb":4,"rt":null},{"n":"OnSiegeWeaponDoorClose","c":"Primitive","p":{"instance":"BatteringRam","player":"BasePlayer"},"rb":4,"rt":null},{"n":"OnSiegeWeaponPull","c":"Primitive","p":{"instance":"BaseSiegeWeapon","player":"BasePlayer"},"rb":4,"rt":null},{"n":"OnGibsSpawned","c":"Entity","p":{"list`1":"List<ServerGib>","creator":"UnityEngine.GameObject"},"rb":0,"rt":null},{"n":"OnCrateSpawned","c":"Entity","p":{"instance":"PatrolHelicopter","baseEntity":"BaseEntity"},"rb":0,"rt":null},{"n":"OnFeedbackReported","c":"Player","p":{"basePlayer":"BasePlayer","string":"string","text":"string","reportType":"Facepunch.Models.ReportType"},"rb":0,"rt":null},{"n":"OnPlayerReported","c":"Player","p":{"basePlayer":"BasePlayer","text3":"string","<targetId>5__2":"string","string":"string","text":"string","message":"string"},"rb":0,"rt":null},{"n":"OnTeamMemberPromote","c":"Team","p":{"instance":"RelationshipManager.PlayerTeam","newTeamLeader":"ulong"},"rb":4,"rt":null},{"n":"OnSteamInventoryUpdated","c":"Player","p":{"steamInventory":"SteamInventory"},"rb":0,"rt":null},{"n":"OnTeamMemberInvite","c":"Team","p":{"playerTeam":"RelationshipManager.PlayerTeam","basePlayer":"BasePlayer","uLong":"ulong","true":"bool"},"rb":1,"rt":null},{"n":"OnBookmarkDelete","c":"Bookmark","p":{"instance":"ComputerStation","mountedPlayer":"BasePlayer","identifier":"string"},"rb":1,"rt":null},{"n":"OnMixingTableFinished","c":"Entity","p":{"instance":"MixingTable","MixStartingPlayer":"BasePlayer","recipe":"Recipe","quantity":"int"},"rb":0,"rt":null},{"n":"OnCuiDraggableDrag","c":"CommunityUI","p":{"player":"BasePlayer","name":"string","position":"UnityEngine.Vector3","type":"CommunityEntity.DraggablePositionSendType"},"rb":0,"rt":null},{"n":"OnCuiDraggableDrop","c":"CommunityUI","p":{"player":"BasePlayer","draggedName":"string","draggedSlot":"string","swappedName":"string","swappedSlot":"string"},"rb":0,"rt":null},{"n":"OnTurretIdentifierSet","c":"Turret","p":{"instance":"AutoTurret","player":"BasePlayer","newID":"string"},"rb":4,"rt":null},{"n":"OnPlayerRevive","c":"Player","p":{"fromPlayer":"BasePlayer","instance":"BasePlayer"},"rb":1,"rt":null},{"n":"OnPlayerUnvanish","c":"Player","p":{"basePlayer":"BasePlayer"},"rb":4,"rt":null},{"n":"OnPlayerUnvanished","c":"Player","p":{"basePlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnSignalBroadcast","c":"Player","p":{"instance":"BaseEntity","sourceConnection":"Network.Connection","signal":"BaseEntity.Signal","arg":"string"},"rb":4,"rt":null},{"n":"CanTeleportDeepSea","c":"Naval","p":{"entity":"BaseEntity","Portal":"DeepSeaPortal"},"rb":1,"rt":"ValueTuple<bool, Translate.Phrase>"},{"n":"OnBoatGroupSpawned","c":"Naval","p":{"instance":"BoatGroupSpawner","vector2":"UnityEngine.Vector2","quaternion":"UnityEngine.Quaternion","list":"HashSet<RHIB>","flag":"bool","spawnsPT":"bool"},"rb":0,"rt":null},{"n":"OnPlayerBoatEditStarted","c":"Naval","p":{"playerBoat":"PlayerBoat","instance":"BoatBuildingStation"},"rb":0,"rt":null},{"n":"CanEditPlayerBoat","c":"Naval","p":{"instance":"PlayerBoat","player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnDeepSeaOpen","c":"Naval","p":{"deepSeaManager":"DeepSeaManager"},"rb":0,"rt":null},{"n":"OnDeepSeaClose","c":"Naval","p":{"deepSeaManager":"DeepSeaManager"},"rb":0,"rt":null},{"n":"OnFogOfWarStale","c":"Player","p":{"instance":"BasePlayer"},"rb":0,"rt":null},{"n":"OnFogOfWarCleared","c":"Player","p":{"instance":"BasePlayer","mainland":"bool","deepSea":"bool"},"rb":0,"rt":null},{"n":"OnFogOfWarImageUpdate","c":"Player","p":{"instance":"BasePlayer","b":"byte","b2":"byte","num":"uint","num2":"uint","byte[]":"byte[]"},"rb":4,"rt":null},{"n":"OnEngineStarted","c":"Vehicle","p":{"instance":"MotorRowboat","basePlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPurchaseMasterKey","c":"Apartments","p":{"player":"BasePlayer","position":"UnityEngine.Vector3"},"rb":1,"rt":null},{"n":"CanAffordMasterKey","c":"Apartments","p":{"player":"BasePlayer"},"rb":1,"rt":"bool"},{"n":"OnRentableShopClose","c":"Apartments","p":{"instance":"RentableShop","notify":"bool"},"rb":1,"rt":null},{"n":"OnRentableShopClosed","c":"Apartments","p":{"instance":"RentableShop","notify":"bool"},"rb":0,"rt":null},{"n":"OnRentableShopOpened","c":"Apartments","p":{"instance":"RentableShop","byPlayer":"BasePlayer"},"rb":0,"rt":null},{"n":"OnRentableShopBreakInComplete","c":"Apartments","p":{"instance":"RentableShop","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnApartmentRoomBreakInComplete","c":"Apartments","p":{"apartmentRoom":"ApartmentRoom","player":"BasePlayer","instance":"ApartmentDoor"},"rb":1,"rt":null},{"n":"OnApartmentRoomUpgrade","c":"Apartments","p":{"playerApartment":"ApartmentRoom","player":"BasePlayer","size":"ApartmentSize","instance":"ApartmentBuilding"},"rb":1,"rt":null},{"n":"OnRentableShopOpen","c":"Apartments","p":{"instance":"RentableShop","player":"BasePlayer"},"rb":1,"rt":null},{"n":"OnApartmentRoomCheckedout","c":"Apartments","p":{"player":"BasePlayer","playerApartment":"ApartmentRoom","instance":"ApartmentBuilding"},"rb":0,"rt":null},{"n":"OnApartmentRoomPurchase","c":"Apartments","p":{"apartmentRoom":"ApartmentRoom","player":"BasePlayer","size":"ApartmentSize","instance":"ApartmentBuilding"},"rb":4,"rt":null},{"n":"OnApartmentRoomUpgraded","c":"Apartments","p":{"apartmentRoom":"ApartmentRoom","player":"BasePlayer","size":"ApartmentSize","instance":"ApartmentBuilding"},"rb":0,"rt":null},{"n":"OnApartmentRoomPurchased","c":"Apartments","p":{"apartmentRoom":"ApartmentRoom","player":"BasePlayer","size":"ApartmentSize","instance":"ApartmentBuilding"},"rb":0,"rt":null},{"n":"OnApartmentRoomBreakInCompleted","c":"Apartments","p":{"apartmentRoom":"ApartmentRoom","player":"BasePlayer","instance":"ApartmentDoor"},"rb":0,"rt":null},{"n":"OnRentableShopBreakInCompleted","c":"Apartments","p":{"instance":"RentableShop","player":"BasePlayer"},"rb":0,"rt":null},{"n":"OnPlayerVoice","c":"Player","p":{"basePlayer":"BasePlayer","arraySegment`1":"ArraySegment<byte>"},"rb":1,"rt":null},{"n":"IOnServerCommand","c":"Server","p":{"arg":"ConsoleSystem.Arg"},"rb":1,"rt":"bool"},{"n":"IOnRunCommandLine","c":"Server","p":{},"rb":1,"rt":null},{"n":"IOnRconMessage","c":"Server","p":{"ClientIpAddress":"System.Net.IPAddress","s":"string"},"rb":1,"rt":null},{"n":"OnClientDisconnected","c":"Player","p":{"cn":"Network.Connection","strReason":"string"},"rb":0,"rt":null},{"n":"OnClanDisbanded","c":"Clan","p":{"localClan":"LocalClan","bySteamId":"ulong"},"rb":0,"rt":null},{"n":"OnClanCreated","c":"Clan","p":{"localClan":"LocalClan","leaderSteamId":"ulong"},"rb":0,"rt":null},{"n":"OnClanMemberAdded","c":"Clan","p":{"clanId":"long","steamId":"ulong"},"rb":0,"rt":null},{"n":"OnClanMemberLeft","c":"Clan","p":{"localClan":"LocalClan","steamId":"ulong"},"rb":0,"rt":null},{"n":"OnClanMemberKicked","c":"Clan","p":{"localClan":"LocalClan","steamId":"ulong","bySteamId":"ulong"},"rb":0,"rt":null},{"n":"OnClanColorChanged","c":"Clan","p":{"localClan":"LocalClan","newColor":"UnityEngine.Color32","bySteamId":"ulong"},"rb":0,"rt":null},{"n":"OnClanLogoChanged","c":"Clan","p":{"localClan":"LocalClan","newLogo":"byte[]","bySteamId":"ulong"},"rb":0,"rt":null}];
const HOOK_BY_NAME = new Map(HOOK_DATA.map(h => [h.n, h]));

function buildHookSnippet(hook) {
  const params = Object.entries(hook.p || {}).map(([name, type]) => `${type} ${name}`).join(', ');
  if (hook.rb === 0) {
    return `    private void ${hook.n}(${params})
    {
    }`;
  }
  const hint = hook.rt
    ? `// return a ${hook.rt} to override, or null to allow default behavior`
    : `// return non-null to override default behavior, or null to allow it`;
  return `    private object ${hook.n}(${params})
    {
        ${hint}
        return null;
    }`;
}

if (hookInsert) {
  const byCategory = new Map();
  for (const hook of HOOK_DATA) {
    if (!byCategory.has(hook.c)) byCategory.set(hook.c, []);
    byCategory.get(hook.c).push(hook);
  }
  const categories = Array.from(byCategory.keys()).sort((a, b) => a.localeCompare(b));
  const frag = document.createDocumentFragment();
  for (const cat of categories) {
    const group = document.createElement('optgroup');
    group.label = cat;
    const hooksInCat = byCategory.get(cat).slice().sort((a, b) => a.n.localeCompare(b.n));
    for (const hook of hooksInCat) {
      const opt = document.createElement('option');
      opt.value = 'hook:' + hook.n;
      opt.textContent = hook.n;
      group.appendChild(opt);
    }
    frag.appendChild(group);
  }
  hookInsert.appendChild(frag);
}

if (hookSearch && hookInsert) {
  hookSearch.addEventListener('input', () => {
    const q = hookSearch.value.trim().toLowerCase();
    Array.from(hookInsert.querySelectorAll('optgroup')).forEach(group => {
      let anyVisible = false;
      Array.from(group.children).forEach(opt => {
        const match = !q || opt.textContent.toLowerCase().includes(q);
        opt.hidden = !match;
        if (match) anyVisible = true;
      });
      group.hidden = !anyVisible;
    });
  });
}
function insertSnippet(code) {
  if (cm) {
    const doc = cm.getDoc();
    const cursor = doc.getCursor();
    doc.replaceRange('\n' + code + '\n', cursor);
    cm.focus();
  } else if (textarea) {
    const start = textarea.selectionStart || 0;
    const end   = textarea.selectionEnd || 0;
    const before = textarea.value.slice(0, start);
    const after  = textarea.value.slice(end);
    textarea.value = before + '\n' + code + '\n' + after;
    textarea.selectionStart = textarea.selectionEnd = start + code.length + 2;
    textarea.focus();
  }
  lastGeneratedTemplate = null; 
  updateStatus();
}
if (hookInsert) {
  hookInsert.addEventListener('change', () => {
    const key = hookInsert.value;
    if (key.startsWith('hook:')) {
      const hook = HOOK_BY_NAME.get(key.slice(5));
      if (hook) insertSnippet(buildHookSnippet(hook));
    } else if (key && SNIPPETS[key]) {
      insertSnippet(SNIPPETS[key].code);
    }
    hookInsert.value = '';
    if (hookSearch) hookSearch.value = '';
    Array.from(hookInsert.querySelectorAll('optgroup, option')).forEach(el => { el.hidden = false; });
  });
}

let cm = null;
let savedSnapshot = textarea ? textarea.value : '';

function getValue()        { return cm ? cm.getValue() : (textarea ? textarea.value : ''); }
function setValue(v)       { if (cm) cm.setValue(v); else if (textarea) textarea.value = v; }
function syncToHiddenField() {
  if (cm) cm.save(); 
  if (sourceField && textarea) sourceField.value = textarea.value;
}
function triggerAction(action) {
  if (actionField) actionField.value = action;
  syncToHiddenField();
  if (form) { form.requestSubmit ? form.requestSubmit() : form.submit(); }
}

if (textarea) {
  if (window.CodeMirror) {
    cm = CodeMirror.fromTextArea(textarea, {
      mode: 'text/x-csharp',
      theme: 'material-darker',
      lineNumbers: true,
      indentUnit: 4,
      tabSize: 4,
      indentWithTabs: false,
      matchBrackets: true,
      autoCloseBrackets: true,
      styleActiveLine: true,
      foldGutter: true,
      gutters: ['CodeMirror-linenumbers', 'CodeMirror-foldgutter'],
      lineWrapping: false,
      extraKeys: {
        'Ctrl-Enter': () => { triggerAction('validate'); return false; },
        'Cmd-Enter':  () => { triggerAction('validate'); return false; },
        'Tab': (instance) => {
          if (instance.somethingSelected()) instance.indentSelection('add');
          else instance.replaceSelection('    ', 'end');
        }
      }
    });
    cm.setSize('100%', 480);

    errorLines.forEach(lineNo => {
      const idx = lineNo - 1;
      if (idx < 0 || idx >= cm.lineCount()) return;
      cm.addLineClass(idx, 'background', 'cm-error-line');
      cm.addLineClass(idx, 'gutter', 'cm-error-line-gutter');
    });

    cm.on('cursorActivity', updateStatus);
    cm.on('change', updateStatus);

    window.jumpToLine = function (lineNo) {
      const line = Math.max(0, lineNo - 1);
      cm.setCursor({ line: line, ch: 0 });
      cm.scrollIntoView({ line: line, ch: 0 }, 100);
      cm.focus();
    };
  } else {
    textarea.addEventListener('input', updateStatus);
    textarea.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); triggerAction('validate'); }
    });

    window.jumpToLine = function (lineNo) {
      const lines = textarea.value.split('\n');
      let charIndex = 0;
      for (let i = 0; i < lineNo - 1 && i < lines.length; i++) charIndex += lines[i].length + 1;
      textarea.focus();
      textarea.setSelectionRange(charIndex, charIndex + (lines[lineNo - 1] ? lines[lineNo - 1].length : 0));
    };
  }

  function updateStatus() {
    const value = getValue();
    if (cm) {
      const pos = cm.getCursor();
      const statusPosEl = document.getElementById('statusPos');
      if (statusPosEl) statusPosEl.textContent = 'Ln ' + (pos.line + 1) + ', Col ' + (pos.ch + 1);
    }
    const statusLenEl = document.getElementById('statusLen');
    if (statusLenEl) statusLenEl.textContent = value.length.toLocaleString() + ' chars';
    if (dirtyDot) dirtyDot.classList.toggle('show', value !== savedSnapshot);
  }
  updateStatus();

  if (fileInput) {
    fileInput.addEventListener('change', () => {
      const file = fileInput.files[0];
      if (!file) return;
      const reader = new FileReader();
      reader.onload = () => {
        setValue(reader.result);
        if (fileNameField) fileNameField.value = file.name;
        if (fnameLabel) fnameLabel.textContent = file.name;
        if (statusFile) statusFile.textContent = file.name;
        lastGeneratedTemplate = null; 
        updateStatus();
      };
      reader.readAsText(file);
    });
  }

  if (newFileBtn) {
    newFileBtn.addEventListener('click', () => {
      if (getValue().trim() !== '' && !confirm('Clear the editor and start a new plugin? Anything unsaved will be lost.')) {
        return;
      }
      const rawName = prompt('Plugin name (used for the [Info] name and the class name):', 'New Plugin');
      if (rawName === null) return; 

      const displayName = rawName.trim().replace(/\s+/g, ' ') || 'New Plugin';
      const className    = toClassName(displayName);
      const newName      = className + '.cs';
      const template       = buildNewPluginTemplate(className, displayName, lastGeneratedAuthor);

      setValue(template);
      lastGeneratedTemplate = template;

      if (fileNameField) fileNameField.value = newName;
      if (fnameLabel) fnameLabel.textContent = newName;
      if (statusFile) statusFile.textContent = newName;
      updateStatus();
      if (cm) cm.focus(); else textarea.focus();
    });
  }

  if (fileNameField) {
    fileNameField.addEventListener('input', () => {
      if (fnameLabel) fnameLabel.textContent = fileNameField.value;
      if (statusFile) statusFile.textContent = fileNameField.value;

      if (lastGeneratedTemplate !== null && getValue() === lastGeneratedTemplate) {
        const className = toClassName(fileNameField.value);
        const infoName  = toInfoName(fileNameField.value);
        const template   = buildNewPluginTemplate(className, infoName, lastGeneratedAuthor);
        setValue(template);
        lastGeneratedTemplate = template;
        updateStatus();
      }
    });
  }

  document.getElementById('validateBtn')?.addEventListener('click', (e) => {
    e.preventDefault();
    triggerAction('validate');
  });

  if (form) {
    form.addEventListener('submit', syncToHiddenField);
  }

  window.addEventListener('beforeunload', (e) => {
    if (getValue() !== savedSnapshot) {
      e.preventDefault();
      e.returnValue = '';
    }
  });
}

const bulkFileInput  = document.getElementById('bulkFileInput');
const bulkFileList   = document.getElementById('bulkFileList');
const bulkDrop       = document.getElementById('bulkDrop');
const bulkForm       = document.getElementById('bulkForm');
const bulkSubmit     = document.getElementById('bulkSubmit');
const bulkSummary    = document.getElementById('bulkSummary');
const bulkTableWrap  = document.getElementById('bulkTableWrap');
const bulkTableBody  = document.getElementById('bulkTableBody');
const APP_MAX_FILES  = <?= (int)MAX_BULK_FILES ?>;
const PER_FILE_MAX   = <?= (int)MAX_UPLOAD_BYTES ?>;
const CONCURRENCY    = <?= (int)BULK_CONCURRENCY ?>;
const originalTitle  = document.title;

function updateBulkTitle(done, total) {
  if (!total) { document.title = originalTitle; return; }
  const pct = Math.round((done / total) * 100);
  document.title = `${pct}% [${done}/${total}] ${originalTitle}`;
}

if (bulkFileInput) {
  function fmtBytes(n) {
    if (n < 1024)        return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(2) + ' MB';
  }

  function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => (
      { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
    ));
  }

  function renderBulkList(files) {
    bulkFileList.innerHTML = '';
    let total = 0;
    let tooBig = 0;
    Array.from(files).forEach(f => {
      total += f.size;
      if (f.size > PER_FILE_MAX) tooBig++;
      const li  = document.createElement('li');
      const nm  = document.createElement('span');
      const sz  = document.createElement('span');
      nm.textContent = f.name + (f.size > PER_FILE_MAX ? '  (over per-file limit)' : '');
      sz.className   = 'fsize' + (f.size > PER_FILE_MAX ? ' fsize-warn' : '');
      sz.textContent = fmtBytes(f.size);
      li.appendChild(nm);
      li.appendChild(sz);
      bulkFileList.appendChild(li);
    });
    if (files.length > 0) {
      const sum = document.createElement('li');
      sum.className = 'bulk-file-summary';
      const left = document.createElement('span');
      left.textContent = files.length + ' file(s) selected   ' + fmtBytes(total) + ' total';
      const right = document.createElement('span');
      right.className = 'fsize';
      if (tooBig > 0) {
        right.textContent = tooBig + ' over ' + fmtBytes(PER_FILE_MAX);
        right.classList.add('fsize-warn');
      } else {
        right.textContent = '';
      }
      sum.appendChild(left);
      sum.appendChild(right);
      bulkFileList.insertBefore(sum, bulkFileList.firstChild);
    }
  }

  bulkFileInput.addEventListener('change', () => renderBulkList(bulkFileInput.files));

  ['dragenter', 'dragover'].forEach(ev => {
    bulkDrop.addEventListener(ev, e => {
      e.preventDefault(); e.stopPropagation();
      bulkDrop.classList.add('drag');
    });
  });
  ['dragleave', 'drop'].forEach(ev => {
    bulkDrop.addEventListener(ev, e => {
      e.preventDefault(); e.stopPropagation();
      bulkDrop.classList.remove('drag');
    });
  });
  bulkDrop.addEventListener('drop', e => {
    const dt = e.dataTransfer;
    if (!dt || !dt.files || dt.files.length === 0) return;
    const out = new DataTransfer();
    Array.from(dt.files).forEach(f => out.items.add(f));
    bulkFileInput.files = out.files;
    renderBulkList(bulkFileInput.files);
  });

  function statusRank(status) {
    if (status === 'fail' || status === 'error') return 0;
    if (status === 'pass') return 2;
    return 1;
  }

  function statusPill(status) {
    if (status === 'pass')  return '<span class="pill ok">pass</span>';
    if (status === 'fail')  return '<span class="pill bad">fail</span>';
    if (status === 'error') return '<span class="pill warn">error</span>';
    return '<span class="pill pending">queued&hellip;</span>';
  }

  function errorCell(row) {
    if (row.status === 'fail' && row.result && Array.isArray(row.result.errors) && row.result.errors.length) {
      const items = row.result.errors.map(err => {
        const ln = (err.line || 0), pos = (err.position || 0);
        return '<li>[' + ln + ':' + pos + '] ' + escapeHtml(err.message || 'Unknown error') + '</li>';
      }).join('');
      return '<details class="err-toggle"><summary>' + row.result.errors.length + ' error(s)</summary>' +
             '<ul class="errlist">' + items + '</ul></details>';
    }
    if (row.status === 'error') {
      return '<span style="color: var(--warn);">' + escapeHtml(row.error || 'Unknown error') + '</span>';
    }
    if (row.status === 'pass') return '<span style="color: var(--muted);">&mdash;</span>';
    return '<span style="color: var(--muted);">waiting&hellip;</span>';
  }

  function elapsedCell(row) {
    const ms = row.result && row.result.elapsedMilliseconds;
    return (ms != null) ? ms + ' ms' : '&mdash;';
  }

  function renderTable(rows) {
    const sorted = rows.map((r, i) => ({ r, i })).sort((a, b) => {
      const ra = statusRank(a.r.status), rb = statusRank(b.r.status);
      return ra !== rb ? ra - rb : a.i - b.i; 
    });
    bulkTableBody.innerHTML = sorted.map(({ r }) => {
      const cls = (r.status === 'fail') ? 'row-fail' : (r.status === 'error') ? 'row-error' : '';
      return '<tr class="' + cls + '">' +
        '<td class="fname-cell">' + escapeHtml(r.name) + '</td>' +
        '<td>' + statusPill(r.status) + '</td>' +
        '<td>' + errorCell(r) + '</td>' +
        '<td>' + elapsedCell(r) + '</td>' +
        '</tr>';
    }).join('');
  }

  function renderSummary(rows, branchLabel, done) {
    const total   = rows.length;
    const pass    = rows.filter(r => r.status === 'pass').length;
    const fail    = rows.filter(r => r.status === 'fail').length;
    const error   = rows.filter(r => r.status === 'error').length;
    const pending = total - pass - fail - error;

    let cls, html;
    if (!done) {
      cls = 'warn';
      const parts = [(total - pending) + ' of ' + total + ' done'];
      if (pass)  parts.push('<strong>' + pass  + '</strong> passed');
      if (fail)  parts.push('<strong>' + fail  + '</strong> failed');
      if (error) parts.push('<strong>' + error + '</strong> unreachable');
      html = 'Validating against <strong>' + escapeHtml(branchLabel) + '</strong>&hellip; ' + parts.join(' &middot; ');
    } else {
      cls = (fail === 0 && error === 0) ? 'ok' : 'bad';
      const parts = [];
      if (pass)  parts.push('<strong>' + pass  + '</strong> passed');
      if (fail)  parts.push('<strong>' + fail  + '</strong> failed');
      if (error) parts.push('<strong>' + error + '</strong> unreachable');
      html = 'Batch against <strong>' + escapeHtml(branchLabel) + '</strong>: ' + parts.join(' &middot; ') + ' of ' + total + '.';
      if (error > 0) {
        html += '<div class="meta">Some files could not be reached &mdash; check the rows below.</div>';
      }
    }
    bulkSummary.className   = 'banner ' + cls;
    bulkSummary.innerHTML   = html;
    bulkSummary.style.display = '';
  }

  async function validateOne(file, branch) {
    const fd = new FormData();
    fd.append('action', 'validate_bulk_item');
    fd.append('branch', branch);
    fd.append('name', file.name);
    fd.append('file', file, file.name);
    try {
      const res  = await fetch(window.location.href, { method: 'POST', body: fd });
      const data = await res.json();
      return data && data.status ? data : { status: 'error', error: 'Malformed response from the server.' };
    } catch (err) {
      return { status: 'error', error: 'Network error while contacting the server.' };
    }
  }

  async function runBulk(files, branch) {
    const rows = files.map(f => {
      const oversized = f.size > PER_FILE_MAX;
      const empty     = f.size === 0;
      return {
        name: f.name,
        status: (oversized || empty) ? 'error' : 'pending',
        error: empty ? 'File is empty.' : (oversized ? 'File is larger than ' + (PER_FILE_MAX / 1024 / 1024) + ' MB.' : null),
        result: null,
        _file: f,
      };
    });

    bulkTableWrap.style.display = '';
    renderTable(rows);
    renderSummary(rows, branch, false);
    const total = rows.length;
    const doneCount = () => rows.filter(r => r.status !== 'pending').length;
    updateBulkTitle(doneCount(), total);
    const queue = rows
      .map((row, idx) => ({ row, idx }))
      .filter(({ row }) => row.status === 'pending');
    let cursor = 0;

    async function worker() {
      while (cursor < queue.length) {
        const { row } = queue[cursor++];
        const data = await validateOne(row._file, branch);
        row.status = data.status || 'error';
        row.error  = data.error || null;
        row.result = data.result || null;
        renderTable(rows);
        renderSummary(rows, branch, false);
        updateBulkTitle(doneCount(), total);
      }
    }

    const workerCount = Math.max(1, Math.min(CONCURRENCY, queue.length || 1));
    await Promise.all(Array.from({ length: workerCount }, worker));

    renderTable(rows);
    renderSummary(rows, branch, true);
    updateBulkTitle(0, 0);
  }

  bulkForm.addEventListener('submit', e => {
    e.preventDefault();

    const files = Array.from(bulkFileInput.files);
    const n     = files.length;
    if (n === 0) {
      alert('Pick at least one .cs file first.');
      return;
    }
    if (n > APP_MAX_FILES) {
      alert('You picked ' + n + ' files but the app cap is ' + APP_MAX_FILES + ' per batch (MAX_BULK_FILES in code).\nSplit the batch.');
      return;
    }

    const branchInput = bulkForm.querySelector('input[name="branch"]:checked');
    const branch       = branchInput ? branchInput.value : 'main';

    bulkSubmit.disabled    = true;
    bulkSubmit.textContent = 'Validating ' + n + ' file(s)\u2026';

    runBulk(files, branch).finally(() => {
      bulkSubmit.disabled    = false;
      bulkSubmit.textContent = 'Validate All';
    });
  });
}
</script>
</body>
</html>
```
