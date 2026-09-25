# Run elevated once on each VM if Host cannot add rules.
netsh advfirewall firewall delete rule name="MWB-SWB Control TCP 15200" >$null 2>&1
netsh advfirewall firewall delete rule name="MWB-SWB Audio UDP 15201" >$null 2>&1
netsh advfirewall firewall delete rule name="MWB-SWB Discovery UDP 15202" >$null 2>&1
netsh advfirewall firewall add rule name="MWB-SWB Control TCP 15200" dir=in action=allow protocol=TCP localport=15200 profile=any
netsh advfirewall firewall add rule name="MWB-SWB Audio UDP 15201" dir=in action=allow protocol=UDP localport=15201 profile=any
netsh advfirewall firewall add rule name="MWB-SWB Discovery UDP 15202" dir=in action=allow protocol=UDP localport=15202 profile=any
Write-Host "SWB firewall rules installed (15200/15201/15202)."
