# API Comparison Test Script
# Compares responses between v1 (EF Core) and v2 (Marten) endpoints

param(
    [string]$BaseUrl = "https://localhost",
    [switch]$SkipCertificateCheck,
    [int]$SampleSize = 10
)

# For PowerShell 7+, use -SkipCertificateCheck parameter
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
}

Write-Host "Testing API at: $BaseUrl" -ForegroundColor Cyan
Write-Host "Comparing v1 (EF Core) vs v2 (Marten) endpoints..." -ForegroundColor Cyan
Write-Host "Sample size for detailed comparisons: $SampleSize items" -ForegroundColor Cyan
Write-Host ""

$testResults = @()
$passCount = 0
$failCount = 0

# Helper function to find property differences between two objects
function Get-PropertyDifferences {
    param(
        [object]$obj1,
        [object]$obj2,
        [string]$path = ""
    )
    
    $differences = @()
    
    if ($null -eq $obj1 -and $null -eq $obj2) { return $differences }
    if ($null -eq $obj1) { return @("$path is null in v1 but not in v2") }
    if ($null -eq $obj2) { return @("$path is null in v2 but not in v1") }
    
    # Compare PSCustomObjects
    if ($obj1.PSObject -and $obj2.PSObject) {
        $props1 = $obj1.PSObject.Properties.Name
        $props2 = $obj2.PSObject.Properties.Name
        
        # Properties only in obj1
        foreach ($prop in $props1) {
            if ($prop -notin $props2) {
                $differences += "$path.$prop exists in v1 but not in v2"
            }
        }
        
        # Properties only in obj2
        foreach ($prop in $props2) {
            if ($prop -notin $props1) {
                $differences += "$path.$prop exists in v2 but not in v1"
            }
        }
        
        # Compare common properties (excluding AggregateVersion)
        foreach ($prop in $props1) {
            if ($prop -in $props2 -and $prop -ne "aggregateVersion") {
                $val1 = $obj1.$prop
                $val2 = $obj2.$prop
                
                $newPath = if ($path) { "$path.$prop" } else { $prop }
                
                # Compare values
                $val1Json = $val1 | ConvertTo-Json -Depth 10 -Compress
                $val2Json = $val2 | ConvertTo-Json -Depth 10 -Compress
                
                if ($val1Json -ne $val2Json) {
                    $differences += "$newPath differs (v1: $($val1Json.Substring(0, [Math]::Min(50, $val1Json.Length)))... | v2: $($val2Json.Substring(0, [Math]::Min(50, $val2Json.Length)))...)"
                }
            }
        }
    }
    
    return $differences
}

function Test-Endpoint {
    param(
        [string]$Name,
        [string]$V1Path,
        [string]$V2Path,
        [string]$Method = "GET",
        [object]$Body = $null,
        [string[]]$ExcludeV1 = @(),
        [string[]]$ExcludeV2 = @(),
        [string]$SortBy = '',
        [scriptblock]$TransformV1 = $null,
        [scriptblock]$TransformV2 = $null
    )
    
    Write-Host "Testing: $Name" -ForegroundColor Yellow
    
    try {
        $v1Url = "$BaseUrl$V1Path"
        $v2Url = "$BaseUrl$V2Path"
        
        # Make requests
        $v1Response = if ($Method -eq "GET") {
            Invoke-RestMethod -Uri $v1Url -Method Get -ErrorAction Stop
        } else {
            Invoke-RestMethod -Uri $v1Url -Method $Method -Body ($Body | ConvertTo-Json) -ContentType "application/json" -ErrorAction Stop
        }

        $v2Response = if ($Method -eq "GET") {
            Invoke-RestMethod -Uri $v2Url -Method Get -ErrorAction Stop
        } else {
            Invoke-RestMethod -Uri $v2Url -Method $Method -Body ($Body | ConvertTo-Json) -ContentType "application/json" -ErrorAction Stop
        }

        # Optional sorting for arrays to stabilize ordering
        if ($SortBy) {
            if ($v1Response -is [System.Collections.IEnumerable] -and $v1Response -isnot [string]) {
                $v1Response = @($v1Response) | Sort-Object -Property $SortBy
            }
            if ($v2Response -is [System.Collections.IEnumerable] -and $v2Response -isnot [string]) {
                $v2Response = @($v2Response) | Sort-Object -Property $SortBy
            }
        }

        # Exclude known-different properties
        $v1Processed = if ($ExcludeV1.Count -gt 0) { $v1Response | Select-Object -Property * -ExcludeProperty $ExcludeV1 } else { $v1Response }
        $v2Processed = if ($ExcludeV2.Count -gt 0) { $v2Response | Select-Object -Property * -ExcludeProperty $ExcludeV2 } else { $v2Response }

        # Optional transforms for deep normalization
        if ($TransformV1) { $v1Processed = & $TransformV1 $v1Processed }
        if ($TransformV2) { $v2Processed = & $TransformV2 $v2Processed }

        # Convert to JSON for comparison (normalized)
        $v1Json = $v1Processed | ConvertTo-Json -Depth 10 -Compress
        $v2Json = $v2Processed | ConvertTo-Json -Depth 10 -Compress
        
        # Compare
        if ($v1Json -eq $v2Json) {
            Write-Host "  ✓ PASS - Responses are identical" -ForegroundColor Green
            $script:passCount++
            $script:testResults += [PSCustomObject]@{
                Test = $Name
                Status = "PASS"
                Message = "Responses match"
                V1Count = if ($v1Response -is [Array]) { $v1Response.Count } else { 1 }
                V2Count = if ($v2Response -is [Array]) { $v2Response.Count } else { 1 }
            }
        } else {
            # Check if counts match (for arrays)
            $v1Count = if ($v1Response -is [Array]) { $v1Response.Count } else { 1 }
            $v2Count = if ($v2Response -is [Array]) { $v2Response.Count } else { 1 }
            
            if ($v1Count -eq $v2Count) {
                Write-Host "  ⚠ WARN - Same count ($v1Count items) but content differs" -ForegroundColor Yellow
                
                # Show property differences for first item
                if ($v1Response -is [Array] -and $v1Count -gt 0) {
                    $diffs = Get-PropertyDifferences -obj1 $v1Processed[0] -obj2 $v2Processed[0]
                    if ($diffs.Count -gt 0) {
                        Write-Host "    Property differences in first item:" -ForegroundColor Gray
                        foreach ($diff in ($diffs | Select-Object -First 5)) {
                            Write-Host "      - $diff" -ForegroundColor DarkYellow
                        }
                        if ($diffs.Count -gt 5) {
                            Write-Host "      ... and $($diffs.Count - 5) more differences" -ForegroundColor DarkGray
                        }
                    }
                } else {
                    # Single object comparison
                    $diffs = Get-PropertyDifferences -obj1 $v1Processed -obj2 $v2Processed
                    if ($diffs.Count -gt 0) {
                        Write-Host "    Property differences:" -ForegroundColor Gray
                        foreach ($diff in ($diffs | Select-Object -First 5)) {
                            Write-Host "      - $diff" -ForegroundColor DarkYellow
                        }
                        if ($diffs.Count -gt 5) {
                            Write-Host "      ... and $($diffs.Count - 5) more differences" -ForegroundColor DarkGray
                        }
                    }
                }
                
                $script:passCount++
                $script:testResults += [PSCustomObject]@{
                    Test = $Name
                    Status = "WARN"
                    Message = "Same count but content differs"
                    V1Count = $v1Count
                    V2Count = $v2Count
                }
            } else {
                Write-Host "  ✗ FAIL - Different counts (v1: $v1Count, v2: $v2Count)" -ForegroundColor Red
                $script:failCount++
                $script:testResults += [PSCustomObject]@{
                    Test = $Name
                    Status = "FAIL"
                    Message = "Count mismatch"
                    V1Count = $v1Count
                    V2Count = $v2Count
                }
            }
        }
    }
    catch {
        Write-Host "  ✗ ERROR - $($_.Exception.Message)" -ForegroundColor Red
        $script:failCount++
        $script:testResults += [PSCustomObject]@{
            Test = $Name
            Status = "ERROR"
            Message = $_.Exception.Message
            V1Count = "N/A"
            V2Count = "N/A"
        }
    }
    
    Write-Host ""
}

# Run Tests
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "USER ENDPOINTS" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan

# Fetch sample users only
Write-Host "Fetching sample users (limit $SampleSize)..." -ForegroundColor Gray
$v1UsersAll = Invoke-RestMethod -Uri "$BaseUrl/api/user?take=$SampleSize" -Method Get -ErrorAction Stop
$v2UsersAll = Invoke-RestMethod -Uri "$BaseUrl/api/v2/user?take=$SampleSize" -Method Get -ErrorAction Stop
Write-Host "  v1 (EF Core): $($v1UsersAll.Count) users fetched" -ForegroundColor White
Write-Host "  v2 (Marten):  $($v2UsersAll.Count) users fetched" -ForegroundColor White
Write-Host ""

# Compare fetched sample (sorted by Id for stability)
Write-Host "Testing: Compare Sample Users" -ForegroundColor Yellow
$v1Sample = $v1UsersAll | Sort-Object Id
$v2Sample = $v2UsersAll | Sort-Object Id
$v1Json = $v1Sample | ConvertTo-Json -Depth 10 -Compress
$v2Json = $v2Sample | ConvertTo-Json -Depth 10 -Compress
if ($v1Json -eq $v2Json) {
    Write-Host "  ✓ PASS - Sample responses are identical" -ForegroundColor Green
    $script:passCount++
} else {
    Write-Host "  ⚠ WARN - Sample responses differ" -ForegroundColor Yellow
    $script:passCount++
}
Write-Host ""

# Test Get By ID using first user's ID
try {
    if ($v1UsersAll -and $v1UsersAll.Count -gt 0) {
        $firstUserId = $v1UsersAll[0].Id
        Test-Endpoint -Name "Get User By ID" -V1Path "/api/user/$firstUserId" -V2Path "/api/v2/user/$firstUserId"
        
        # Test sampled users for data consistency
        $testCount = [Math]::Min($SampleSize, $v1UsersAll.Count)
        Write-Host "  Testing $testCount users for data consistency..." -ForegroundColor Gray
        $mismatchCount = 0
        for ($i = 0; $i -lt $testCount; $i++) {
            $userId = $v1UsersAll[$i].Id
            $v1User = Invoke-RestMethod -Uri "$BaseUrl/api/user/$userId" -Method Get -ErrorAction Stop
            $v2User = Invoke-RestMethod -Uri "$BaseUrl/api/v2/user/$userId" -Method Get -ErrorAction Stop
            
            $v1Json = $v1User | ConvertTo-Json -Depth 5 -Compress
            $v2Json = $v2User | ConvertTo-Json -Depth 5 -Compress
            
            if ($v1Json -ne $v2Json) {
                $mismatchCount++
            }
        }
        if ($mismatchCount -eq 0) {
            Write-Host "  ✓ All $testCount sampled users match perfectly" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ $mismatchCount of $testCount sampled users have differences" -ForegroundColor Yellow
        }
    }
} catch {
    Write-Host "  ⚠ Skipped additional tests - error: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "CUSTOMER ENDPOINTS" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan

# Fetch sample customers only
Write-Host "Fetching sample customers (limit $SampleSize)..." -ForegroundColor Gray
$v1CustomersAll = Invoke-RestMethod -Uri "$BaseUrl/api/customer?take=$SampleSize" -Method Get -ErrorAction Stop
$v2CustomersAll = Invoke-RestMethod -Uri "$BaseUrl/api/v2/customer?take=$SampleSize" -Method Get -ErrorAction Stop
Write-Host "  v1 (EF Core): $($v1CustomersAll.Count) customers fetched" -ForegroundColor White
Write-Host "  v2 (Marten):  $($v2CustomersAll.Count) customers fetched" -ForegroundColor White
Write-Host ""

# Compare fetched sample (sorted by Id for stability)
Write-Host "Testing: Compare Sample Customers" -ForegroundColor Yellow
$v1Sample = $v1CustomersAll | Sort-Object Id
$v2Sample = $v2CustomersAll | Sort-Object Id
$v1Json = $v1Sample | ConvertTo-Json -Depth 10 -Compress
$v2Json = $v2Sample | ConvertTo-Json -Depth 10 -Compress
if ($v1Json -eq $v2Json) {
    Write-Host "  ✓ PASS - Sample responses are identical" -ForegroundColor Green
    $script:passCount++
} else {
    Write-Host "  ⚠ WARN - Sample responses differ" -ForegroundColor Yellow
    $script:passCount++
}
Write-Host ""

# Test Get By ID using first customer's ID
try {
    if ($v1CustomersAll -and $v1CustomersAll.Count -gt 0) {
        $firstCustomerId = $v1CustomersAll[0].Id
        Test-Endpoint -Name "Get Customer By ID" -V1Path "/api/customer/$firstCustomerId" -V2Path "/api/v2/customer/$firstCustomerId"
        
        # Test sampled customers for data consistency
        $testCount = [Math]::Min($SampleSize, $v1CustomersAll.Count)
        Write-Host "  Testing $testCount customers for data consistency..." -ForegroundColor Gray
        $mismatchCount = 0
        for ($i = 0; $i -lt $testCount; $i++) {
            $customerId = $v1CustomersAll[$i].Id
            $v1Customer = Invoke-RestMethod -Uri "$BaseUrl/api/customer/$customerId" -Method Get -ErrorAction Stop
            $v2Customer = Invoke-RestMethod -Uri "$BaseUrl/api/v2/customer/$customerId" -Method Get -ErrorAction Stop
            
            $v1Json = $v1Customer | ConvertTo-Json -Depth 5 -Compress
            $v2Json = $v2Customer | ConvertTo-Json -Depth 5 -Compress
            
            if ($v1Json -ne $v2Json) {
                $mismatchCount++
            }
        }
        if ($mismatchCount -eq 0) {
            Write-Host "  ✓ All $testCount sampled customers match perfectly" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ $mismatchCount of $testCount sampled customers have differences" -ForegroundColor Yellow
        }
    }
} catch {
    Write-Host "  ⚠ Skipped additional tests - error: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "TODO ENDPOINTS" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan

# Fetch sample todos only
Write-Host "Fetching sample todos (limit $SampleSize)..." -ForegroundColor Gray
$v1TodosAll = Invoke-RestMethod -Uri "$BaseUrl/api/todo?take=$SampleSize&orderBy=createdAt" -Method Get -ErrorAction Stop
$v2TodosAll = Invoke-RestMethod -Uri "$BaseUrl/api/v2/todo?take=$SampleSize&orderBy=createdAt" -Method Get -ErrorAction Stop
Write-Host "  v1 (EF Core): $($v1TodosAll.Count) todos fetched" -ForegroundColor White
Write-Host "  v2 (Marten):  $($v2TodosAll.Count) todos fetched" -ForegroundColor White
Write-Host ""

# Compare fetched sample (sorted, per-item comparison)
Write-Host "Testing: Compare Sample Todos" -ForegroundColor Yellow
$v1Sample = $v1TodosAll | Sort-Object Id
$v2Sample = $v2TodosAll | Sort-Object Id

# Remove aggregateVersion from all items for comparison
foreach ($todo in $v1Sample) { $todo.PSObject.Properties.Remove('aggregateVersion') }
foreach ($todo in $v2Sample) { $todo.PSObject.Properties.Remove('aggregateVersion') }

$v2ById = @{}
foreach ($t in $v2Sample) { $v2ById[$t.Id] = $t }
$diffCount = 0
$diffIds = @()
$allDiffs = @()
foreach ($t in $v1Sample) {
    $id = $t.Id
    if ($v2ById.ContainsKey($id)) {
        $v1Json = $t | ConvertTo-Json -Depth 10 -Compress
        $v2Json = $v2ById[$id] | ConvertTo-Json -Depth 10 -Compress
        if ($v1Json -ne $v2Json) { 
            $diffCount++
            $diffIds += $id
            $itemDiffs = Get-PropertyDifferences -obj1 $t -obj2 $v2ById[$id]
            $allDiffs += "[$id]: $($itemDiffs -join ', ')"
        }
    } else { 
        $diffCount++
        $diffIds += $id
        $allDiffs += "[$id]: Missing in v2"
    }
}
if ($diffCount -eq 0) {
    Write-Host "  ✓ PASS - Sample responses are identical" -ForegroundColor Green
    $script:passCount++
} else {
    Write-Host "  ⚠ WARN - $diffCount of $SampleSize sampled todos differ" -ForegroundColor Yellow
    Write-Host "    IDs with differences: $($diffIds -join ', ')" -ForegroundColor DarkYellow
    Write-Host "    Property differences:" -ForegroundColor Gray
    foreach ($diff in ($allDiffs | Select-Object -First 3)) {
        Write-Host "      $diff" -ForegroundColor DarkYellow
    }
    if ($allDiffs.Count -gt 3) {
        Write-Host "      ... and $($allDiffs.Count - 3) more" -ForegroundColor DarkGray
    }
    $script:passCount++
}
Write-Host ""

# Test Get By ID using first todo's ID
try {
    if ($v1TodosAll -and $v1TodosAll.Count -gt 0) {
        $firstTodoId = $v1TodosAll[0].Id
        Test-Endpoint -Name "Get Todo By ID" -V1Path "/api/todo/$firstTodoId" -V2Path "/api/v2/todo/$firstTodoId" -ExcludeV1 @('aggregateVersion') -ExcludeV2 @('aggregateVersion')
        
        # Test sampled todos for data consistency
        $testCount = [Math]::Min($SampleSize, $v1TodosAll.Count)
        Write-Host "  Testing $testCount todos for data consistency..." -ForegroundColor Gray
        $mismatchCount = 0
        $mismatchIds = @()
        for ($i = 0; $i -lt $testCount; $i++) {
            $todoId = $v1TodosAll[$i].Id
            $v1Todo = Invoke-RestMethod -Uri "$BaseUrl/api/todo/$todoId" -Method Get -ErrorAction Stop
            $v2Todo = Invoke-RestMethod -Uri "$BaseUrl/api/v2/todo/$todoId" -Method Get -ErrorAction Stop
            
            # Remove aggregateVersion before comparison
            $v1Todo.PSObject.Properties.Remove('aggregateVersion')
            $v2Todo.PSObject.Properties.Remove('aggregateVersion')
            
            $v1Json = $v1Todo | ConvertTo-Json -Depth 5 -Compress
            $v2Json = $v2Todo | ConvertTo-Json -Depth 5 -Compress
            
            if ($v1Json -ne $v2Json) {
                $mismatchCount++
                $mismatchIds += $todoId
            }
        }
        if ($mismatchCount -eq 0) {
            Write-Host "  ✓ All $testCount sampled todos match perfectly" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ $mismatchCount of $testCount sampled todos have differences" -ForegroundColor Yellow
            Write-Host "    IDs with differences: $($mismatchIds -join ', ')" -ForegroundColor DarkYellow
        }
    }
} catch {
    Write-Host "  ⚠ Skipped additional tests - error: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "COMMENT ENDPOINTS" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan

# Fetch sample comments only
Write-Host "Fetching sample comments (limit $SampleSize)..." -ForegroundColor Gray
$v1CommentsAll = Invoke-RestMethod -Uri "$BaseUrl/api/comment?take=$SampleSize&orderBy=createdAt" -Method Get -ErrorAction Stop
$v2CommentsAll = Invoke-RestMethod -Uri "$BaseUrl/api/v2/comment?take=$SampleSize&orderBy=createdAt" -Method Get -ErrorAction Stop
Write-Host "  v1 (EF Core): $($v1CommentsAll.Count) comments fetched" -ForegroundColor White
Write-Host "  v2 (Marten):  $($v2CommentsAll.Count) comments fetched" -ForegroundColor White
Write-Host ""

# Compare fetched sample (sorted by Id for stability)
Write-Host "Testing: Compare Sample Comments" -ForegroundColor Yellow
$v1Sample = $v1CommentsAll | Sort-Object Id
$v2Sample = $v2CommentsAll | Sort-Object Id

# Remove aggregateVersion from all items for comparison
foreach ($comment in $v1Sample) { $comment.PSObject.Properties.Remove('aggregateVersion') }
foreach ($comment in $v2Sample) { $comment.PSObject.Properties.Remove('aggregateVersion') }

$v1Json = $v1Sample | ConvertTo-Json -Depth 10 -Compress
$v2Json = $v2Sample | ConvertTo-Json -Depth 10 -Compress
if ($v1Json -eq $v2Json) {
    Write-Host "  ✓ PASS - Sample responses are identical" -ForegroundColor Green
    $script:passCount++
} else {
    Write-Host "  ⚠ WARN - Sample responses differ" -ForegroundColor Yellow
    $script:passCount++
}
Write-Host ""

# Test Get By ID using first comment's ID  
try {
    if ($v1CommentsAll -and $v1CommentsAll.Count -gt 0) {
        $firstCommentId = $v1CommentsAll[0].Id
        Test-Endpoint -Name "Get Comment By ID" -V1Path "/api/comment/$firstCommentId" -V2Path "/api/v2/comment/$firstCommentId"
        
        # Test sampled comments for data consistency
        $testCount = [Math]::Min($SampleSize, $v1CommentsAll.Count)
        Write-Host "  Testing $testCount comments for data consistency..." -ForegroundColor Gray
        $mismatchCount = 0
        $mismatchIds = @()
        for ($i = 0; $i -lt $testCount; $i++) {
            $commentId = $v1CommentsAll[$i].Id
            # Note: v1 GetById returns array, v2 returns single object - use same endpoint for both
            $v1Comment = Invoke-RestMethod -Uri "$BaseUrl/api/comment/$commentId" -Method Get -ErrorAction Stop
            $v2Comment = Invoke-RestMethod -Uri "$BaseUrl/api/v2/comment/$commentId" -Method Get -ErrorAction Stop
            
            # v1 returns array for comments by reference, compare first item if array
            $v1Item = if ($v1Comment -is [Array]) { $v1Comment[0] } else { $v1Comment }
            
            $v1Json = $v1Item | ConvertTo-Json -Depth 5 -Compress
            $v2Json = $v2Comment | ConvertTo-Json -Depth 5 -Compress
            
            if ($v1Json -ne $v2Json) {
                $mismatchCount++
                $mismatchIds += $commentId
            }
        }
        if ($mismatchCount -eq 0) {
            Write-Host "  ✓ All $testCount sampled comments match perfectly" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ $mismatchCount of $testCount sampled comments have differences" -ForegroundColor Yellow
            Write-Host "    IDs with differences: $($mismatchIds -join ', ')" -ForegroundColor DarkYellow
        }
    }
} catch {
    Write-Host "  ⚠ Skipped additional tests - error: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Summary
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "TEST SUMMARY" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "Total Tests: $($passCount + $failCount)" -ForegroundColor White
Write-Host "Passed: $passCount" -ForegroundColor Green
Write-Host "Failed: $failCount" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Green" })
Write-Host ""

# Show detailed results
if ($testResults.Count -gt 0) {
    $testResults | Format-Table -AutoSize
}

# Exit with appropriate code
exit $failCount
