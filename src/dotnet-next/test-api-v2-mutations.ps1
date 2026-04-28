# V2 API Mutation Testing Script
# Tests POST, PUT, DELETE operations for the v2 (Marten) API
#
# Usage:
#   .\test-api-v2-mutations.ps1                      # Test v2 endpoints at /api/v2 (default)
#   .\test-api-v2-mutations.ps1 -BasePath "api"      # Test v2 endpoints at /api (requires UseNewEndpoints=true)
#   .\test-api-v2-mutations.ps1 -BaseUrl "https://localhost:5001" -BasePath "api/v2"

param(
    [string]$BaseUrl = "https://localhost",
    [string]$BasePath = "api/v2",
    [switch]$SkipCertificateCheck
)

# For PowerShell 7+, use -SkipCertificateCheck parameter
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
    $PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true
}

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "V2 API Mutation Testing" -ForegroundColor Cyan
Write-Host "Testing API at: $BaseUrl" -ForegroundColor Cyan
Write-Host "Base Path: $BasePath" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

$testResults = @()

# Helper function to test an endpoint
function Test-Endpoint {
    param(
        [string]$Name,
        [scriptblock]$TestBlock
    )
    
    Write-Host "Testing: $Name" -ForegroundColor Yellow
    try {
        $result = & $TestBlock
        Write-Host "  ✓ PASS" -ForegroundColor Green
        $script:testResults += [PSCustomObject]@{
            Test = $Name
            Status = "PASS"
            Message = $result
        }
        return $result
    }
    catch {
        Write-Host "  ✗ FAIL: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Response: $($_.ErrorDetails.Message)" -ForegroundColor DarkRed
        $script:testResults += [PSCustomObject]@{
            Test = $Name
            Status = "FAIL"
            Message = $_.Exception.Message
        }
        throw
    }
}

# Helper function to invoke API with proper error handling
function Invoke-ApiRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body = $null,
        [hashtable]$Headers = @{}
    )
    
    $params = @{
        Method = $Method
        Uri = $Uri
        ContentType = "application/json"
        Headers = $Headers
    }
    
    if ($Body) {
        # If body is an array with a single element, force it to remain an array
        if ($Body -is [Array] -and $Body.Count -eq 1) {
            $params.Body = ($Body | ConvertTo-Json -Depth 10 -AsArray)
        }
        else {
            $params.Body = ($Body | ConvertTo-Json -Depth 10)
        }
    }
    
    return Invoke-RestMethod @params
}

#region User Tests

Write-Host "`n----- USER TESTS -----" -ForegroundColor Cyan

$createdUserId = $null
$createdUser = Test-Endpoint "Create User (POST /$BasePath/user)" {
    $newUser = @{
        firstname = "Test"
        lastname = "User $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    }
    
    $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/user" -Body $newUser
    $script:createdUserId = $response.id
    
    if (-not $response.id) {
        throw "User creation did not return an ID"
    }
    
    "Created user with ID: $($response.id), Name: $($response.firstname) $($response.lastname)"
}

if ($createdUserId) {
    Test-Endpoint "Get Created User (GET /$BasePath/user/{id})" {
        $response = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/user/$createdUserId"
        
        if ($response.id -ne $createdUserId) {
            throw "Retrieved user ID does not match created ID"
        }
        
        "Retrieved user: $($response.firstname) $($response.lastname)"
    }
    
    Test-Endpoint "Update User (PUT /$BasePath/user/{id})" {
        $updateUser = @{
            id = $createdUserId
            firstname = "Updated"
            lastname = "User $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        }
        
        $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/user/$createdUserId" -Body $updateUser
        
        if ($response.firstname -ne "Updated") {
            throw "User firstname was not updated"
        }
        
        "Updated user name to: $($response.firstname) $($response.lastname)"
    }
}

#endregion

#region Customer Tests

Write-Host "`n----- CUSTOMER TESTS -----" -ForegroundColor Cyan

$createdCustomerId = $null
$createdCustomer = Test-Endpoint "Create Customer (POST /$BasePath/customer)" {
    $newCustomer = @{
        name = "Test Customer $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        shortName = "TC-$(Get-Date -Format 'HHmmss')"
    }
    
    $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/customer" -Body $newCustomer
    $script:createdCustomerId = $response.id
    
    if (-not $response.id) {
        throw "Customer creation did not return an ID"
    }
    
    "Created customer with ID: $($response.id), Name: $($response.name)"
}

if ($createdCustomerId) {
    Test-Endpoint "Get Created Customer (GET /$BasePath/customer/{id})" {
        $response = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/customer/$createdCustomerId"
        
        if ($response.id -ne $createdCustomerId) {
            throw "Retrieved customer ID does not match created ID"
        }
        
        "Retrieved customer: $($response.name)"
    }
    
    Test-Endpoint "Update Customer (PUT /$BasePath/customer/{id})" {
        $updateCustomer = @{
            id = $createdCustomerId
            name = "Updated Test Customer $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            shortName = "UTC-$(Get-Date -Format 'HHmmss')"
        }
        
        $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/customer/$createdCustomerId" -Body $updateCustomer
        
        if ($response.name -notlike "Updated Test Customer*") {
            throw "Customer name was not updated"
        }
        
        "Updated customer name to: $($response.name)"
    }
    
    Test-Endpoint "Update Customer IsImportant Flag (PUT /$BasePath/customer/{id})" {
        $updateCustomer = @{
            id = $createdCustomerId
            name = "Updated Test Customer $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            shortName = "UTC-$(Get-Date -Format 'HHmmss')"
            important = $true
        }
        
        $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/customer/$createdCustomerId" -Body $updateCustomer
        
        # Verify the flag was set in the response
        if ($response.important -ne $true) {
            throw "Customer Important flag was not set in response. Got: $($response.important)"
        }
        
        # Get the customer again to verify it was persisted
        $getResponse = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/customer/$createdCustomerId"
        if ($getResponse.important -ne $true) {
            throw "Customer Important flag was not persisted. Got: $($getResponse.important)"
        }
        
        "Updated customer Important flag to: $($getResponse.important)"
    }
    
    # Create second customer for bulk operations
    Test-Endpoint "Create Second Customer for Bulk Tests" {
        $newCustomer = @{
            name = "Bulk Test Customer $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            shortName = "BTC-$(Get-Date -Format 'HHmmss')"
        }
        
        $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/customer" -Body $newCustomer
        $script:createdCustomerId2 = $response.id
        "Created second customer with ID: $($response.id)"
    }
    
    Test-Endpoint "Bulk Archive Customers (PUT /$BasePath/customer/archive)" {
        $archiveBody = @($createdCustomerId, $createdCustomerId2)
        $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/customer/archive" -Body $archiveBody
        "Archived both customers"
    }
    
    Test-Endpoint "Bulk Restore Customers (PUT /$BasePath/customer/archive?restore=true)" {
        $restoreBody = @($createdCustomerId, $createdCustomerId2)
        $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/customer/archive?restore=true" -Body $restoreBody
        "Restored both customers"
    }
    
    Test-Endpoint "Archive Single Customer (POST /$BasePath/customer/archive/{id})" {
        $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/customer/archive/$createdCustomerId"
        "Archived customer by ID"
    }
    
    Test-Endpoint "Restore Single Customer (POST /$BasePath/customer/restore/{id})" {
        $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/customer/restore/$createdCustomerId"
        "Restored customer by ID"
    }
}

#endregion

#region Todo Tests

Write-Host "`n----- TODO TESTS -----" -ForegroundColor Cyan

# Get a user for todo tests
$testUserId = $null
try {
    $users = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/user"
    if ($users -and $users.Count -gt 0) {
        $testUserId = $users[0].id
        Write-Host "Using existing user ID for Todo tests: $testUserId" -ForegroundColor Gray
    }
}
catch {
    Write-Host "Warning: Could not get existing users. Creating a temporary user..." -ForegroundColor Yellow
    $tempUser = @{ firstname = "Temp"; lastname = "User" }
    $userResponse = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/user" -Body $tempUser
    $testUserId = $userResponse.id
}

$createdTodoId = $null
if ($testUserId) {
    $createdTodo = Test-Endpoint "Create Todo (POST /$BasePath/todo)" {
        $newTodo = @{
            title = "Test Todo $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            description = "This is a test todo"
            critical = $false
            awaitingFeedback = $false
            status = 0  # TodoStatus.New
        }
        
        $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/todo" -Body $newTodo
        $script:createdTodoId = $response.id
        
        if (-not $response.id) {
            throw "Todo creation did not return an ID"
        }
        
        "Created todo with ID: $($response.id), Title: $($response.title)"
    }
    
    if ($createdTodoId) {
        Test-Endpoint "Get Created Todo (GET /$BasePath/todo/{id})" {
            $response = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/todo/$createdTodoId"
            
            if ($response.id -ne $createdTodoId) {
                throw "Retrieved todo ID does not match created ID"
            }
            
            "Retrieved todo: $($response.title)"
        }
        
        Test-Endpoint "Update Todo (PUT /$BasePath/todo/{id})" {
            $updateTodo = @{
                id = $createdTodoId
                title = "Updated Test Todo $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
                description = "This is an updated test todo"
                critical = $true
                awaitingFeedback = $false
                status = 0
                responsibles = @()
                isArchived = $false
                childTodosCount = 0
                childTodosUnreadCommentsCount = 0
                lastTouchedAt = (Get-Date).ToUniversalTime().ToString("o")
                unreadComments = 0
                commentsCount = 0
            }
            
            $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/todo/$createdTodoId" -Body $updateTodo
            
            if ($response.title -notlike "Updated Test Todo*") {
                throw "Todo title was not updated"
            }
            
            if ($response.critical -ne $true) {
                throw "Todo critical flag was not set to true"
            }
            
            "Updated todo title to: $($response.title), critical: $($response.critical)"
        }
        
        Test-Endpoint "Update Todo Flags - Add Critical (PATCH /$BasePath/todo/update/flags)" {
            # First clear the critical flag
            $clearFlags = @{
                ids = @($createdTodoId)
                removeFlags = @("Critical")
            }
            Invoke-ApiRequest -Method Patch -Uri "$BaseUrl/$BasePath/todo/update/flags" -Body $clearFlags | Out-Null
            
            # Now add it back
            $addFlags = @{
                ids = @($createdTodoId)
                addFlags = @("Critical")
            }
            $response = Invoke-ApiRequest -Method Patch -Uri "$BaseUrl/$BasePath/todo/update/flags" -Body $addFlags
            
            if ($response[0].critical -ne $true) {
                throw "Critical flag was not set"
            }
            
            "Set critical flag: $($response[0].critical)"
        }
        
        Test-Endpoint "Update Todo Flags - Add AwaitingFeedback (PATCH /$BasePath/todo/update/flags)" {
            $addFlags = @{
                ids = @($createdTodoId)
                addFlags = @("AwaitingFeedback")
            }
            $response = Invoke-ApiRequest -Method Patch -Uri "$BaseUrl/$BasePath/todo/update/flags" -Body $addFlags
            
            if ($response[0].awaitingFeedback -ne $true) {
                throw "AwaitingFeedback flag was not set"
            }
            
            "Set awaitingFeedback flag: $($response[0].awaitingFeedback)"
        }
        
        Test-Endpoint "Update Todo Flags - Remove Multiple (PATCH /$BasePath/todo/update/flags)" {
            $removeFlags = @{
                ids = @($createdTodoId)
                removeFlags = @("Critical", "AwaitingFeedback")
            }
            $response = Invoke-ApiRequest -Method Patch -Uri "$BaseUrl/$BasePath/todo/update/flags" -Body $removeFlags
            
            if ($response[0].critical -ne $false -or $response[0].awaitingFeedback -ne $false) {
                throw "Flags were not removed"
            }
            
            "Removed flags: critical=$($response[0].critical), awaitingFeedback=$($response[0].awaitingFeedback)"
        }
        
        # Create parent and child todos for hierarchy tests
        Test-Endpoint "Create Parent Todo for Hierarchy Tests" {
            $parentTodo = @{
                title = "Parent Todo $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
                description = "Parent todo for hierarchy testing"
                critical = $false
                awaitingFeedback = $false
                status = 0
                responsibles = @()
            }
            
            $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/todo" -Body $parentTodo
            $script:parentTodoId = $response.id
            "Created parent todo with ID: $($response.id)"
        }
        
        Test-Endpoint "Create Child Todo for Hierarchy Tests" {
            $childTodo = @{
                title = "Child Todo $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
                description = "Child todo for hierarchy testing"
                critical = $false
                awaitingFeedback = $false
                status = 0
                responsibles = @()
            }
            
            $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/todo" -Body $childTodo
            $script:childTodoId = $response.id
            "Created child todo with ID: $($response.id)"
        }
        
        Test-Endpoint "Move Child into Parent (POST /$BasePath/todo/{subTodoId}/move-into/{parentTodoId})" {
            $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/todo/$childTodoId/move-into/$parentTodoId"
            "Moved child todo into parent todo"
        }
        
        Test-Endpoint "Verify Child is SubTodo (GET /$BasePath/todo/{id})" {
            $response = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/todo/$childTodoId"
            
            if ($response.parentTodoId -ne $parentTodoId) {
                throw "Child todo parentTodoId does not match parent"
            }
            
            "Verified child todo has parentTodoId: $($response.parentTodoId)"
        }
        
        Test-Endpoint "Convert to Parent (POST /$BasePath/todo/convert-to-parent)" {
            $convertBody = @($childTodoId)
            $response = Invoke-ApiRequest -Method Post -Uri "$BaseUrl/$BasePath/todo/convert-to-parent" -Body $convertBody
            "Converted child todo back to parent todo"
        }
        
        Test-Endpoint "Verify Converted to Parent (GET /$BasePath/todo/{id})" {
            $response = Invoke-ApiRequest -Method Get -Uri "$BaseUrl/$BasePath/todo/$childTodoId"
            
            if ($response.parentTodoId -ne $null) {
                throw "Todo still has parentTodoId after conversion"
            }
            
            "Verified todo is now a parent (no parentTodoId)"
        }
        
        Test-Endpoint "Update Todo Status Bulk (PUT /$BasePath/todo/update/status)" {
            $statusUpdate = @{
                ids = @($createdTodoId, $parentTodoId, $childTodoId)
                status = 1
            }
            $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/todo/update/status" -Body $statusUpdate
            
            if ($response.Count -ne 3) {
                throw "Expected 3 updated todos, got $($response.Count)"
            }
            
            "Updated status for 3 todos to status=1"
        }
        
        Test-Endpoint "Archive Todo (PUT /$BasePath/todo/archive)" {
            $archiveBody = @($createdTodoId)
            $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/todo/archive" -Body $archiveBody
            "Archived todo"
        }
        
        Test-Endpoint "Restore Todo (PUT /$BasePath/todo/archive?restore=true)" {
            $restoreBody = @($createdTodoId)
            $response = Invoke-ApiRequest -Method Put -Uri "$BaseUrl/$BasePath/todo/archive?restore=true" -Body $restoreBody
            "Restored todo"
        }
        
        Test-Endpoint "Delete Todo (DELETE /$BasePath/todo)" {
            $deleteBody = @($createdTodoId)
            $response = Invoke-ApiRequest -Method Delete -Uri "$BaseUrl/$BasePath/todo" -Body $deleteBody
            "Deleted todo"
        }
    }
}
else {
    Write-Host "Skipping Todo tests (no test user available)" -ForegroundColor Yellow
}

#endregion

#region Summary

Write-Host "`n=====================================" -ForegroundColor Cyan
Write-Host "TEST SUMMARY" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan

$passCount = ($testResults | Where-Object { $_.Status -eq "PASS" }).Count
$failCount = ($testResults | Where-Object { $_.Status -eq "FAIL" }).Count

Write-Host "`nTotal Tests: $($testResults.Count)" -ForegroundColor White
Write-Host "Passed: $passCount" -ForegroundColor Green
Write-Host "Failed: $failCount" -ForegroundColor Red

if ($failCount -gt 0) {
    Write-Host "`nFailed Tests:" -ForegroundColor Red
    $testResults | Where-Object { $_.Status -eq "FAIL" } | ForEach-Object {
        Write-Host "  - $($_.Test): $($_.Message)" -ForegroundColor Red
    }
}

Write-Host ""

#endregion



