# Azure deployment fails: "SubscriptionIsOverQuotaForSku" (B1 VMs: limit 0)

This is not a problem with the Add-on Store. The Azure subscription has a quota of
**0 App Service instances of size B1 (Basic)** in the selected region. This is common
for Visual Studio, trial and new subscriptions.

## Fix (pick one)

1. **Request quota (recommended):** Azure portal > *Quotas* > *App Service* > select the
   region > request a limit of **1** for *B1 / Basic*. Alternatively: *Help + support* >
   *New support request* > "Service and subscription limits (quotas)" > quota type
   "App Service". Small increases are usually approved automatically within minutes.
2. **Choose another region** (for example West Europe instead of Germany West Central);
   quota may already be available there.
3. **Choose another size:** the template parameter `sku` also accepts F1, B2, B3, S1 and
   P1V3. S1 or P1V3 often have quota when B1 does not (higher cost). F1 is free but very
   limited (no always-on, limited CPU time) and not tested with the store.
4. **Use a Tungsten company subscription** instead of a personal Visual Studio
   subscription; quotas are usually enabled there, and a productive store belongs there
   anyway (costs and ownership via IT).

Then run the *Deploy to Azure* button again; the template (`infra/azuredeploy.json`)
needs no change.
