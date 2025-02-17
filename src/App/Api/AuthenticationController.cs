﻿// ******************************************************************************
//  © 2018 Sebastiaan Dammann | damsteen.nl
// 
//  File:           : AuthenticationController.cs
//  Project         : App
// ******************************************************************************
using App.Support.Filters;

namespace App.Api {
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using Extensions;
    using Microsoft.ApplicationInsights.AspNetCore.Extensions;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Identity;
    using Microsoft.AspNetCore.Mvc;
    using Models.Domain;
    using Models.Domain.Identity;
    using Models.Domain.Services;
    using Models.DTO;
    using Models.DTO.Services;
    using Support;
    using Support.Hub;
    using Support.Mailing;
    using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

    [Route("api/authentication")]
    public class AuthenticationController : Controller {
        private readonly SignInManager<AppUser> _authenticationManager;
        private readonly AppUserManager _appUserManager;

        private readonly ConfirmEmailMailer _confirmEmailMailer;
        private readonly ForgotPasswordMailer _forgotPasswordMailer;
        private readonly AppUserLoginEventService _appUserLoginEventService;

        private readonly AuthenticationInfoFactory _authenticationInfoFactory;
        private readonly AppOwnerTokenChangeService _appOwnerTokenChangeService;

        public AuthenticationController(AppUserManager appUserManager, SignInManager<AppUser> authenticationManager, ConfirmEmailMailer confirmEmailMailer, ForgotPasswordMailer forgotPasswordMailer, AppUserLoginEventService appUserLoginEventService, AuthenticationInfoFactory authenticationInfoFactory, AppOwnerTokenChangeService appOwnerTokenChangeService) {
            this._appUserManager = appUserManager;
            this._authenticationManager = authenticationManager;
            this._forgotPasswordMailer = forgotPasswordMailer;
            this._appUserLoginEventService = appUserLoginEventService;
            this._authenticationInfoFactory = authenticationInfoFactory;
            this._appOwnerTokenChangeService = appOwnerTokenChangeService;
            this._confirmEmailMailer = confirmEmailMailer;
        }

        [HttpGet("check")]
        [AllowDuringSetup]
        public async Task<AuthenticationInfo> CheckAuthentication() {
            AppUser user = await this._appUserManager.FindByIdAsync(this.User.Identity.GetUserId()).EnsureNotNull(HttpStatusCode.Forbidden);

            return await this._authenticationInfoFactory.CreateAsync(user, this.User);
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody]LoginModel parameters) {
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            AppUser user = await this._appUserManager.FindByNameAsync(parameters.UserName).EnsureNotNull(HttpStatusCode.Forbidden);

            SignInResult result = await this._authenticationManager.PasswordSignInAsync(user, parameters.Password, true, true);
            if (result.Succeeded == false) {
                return this.Ok(new AuthenticationInfo {
                    IsAuthenticated = false,
                    IsLockedOut = result.IsLockedOut,
                    IsTwoFactorAuthenticationRequired = result.RequiresTwoFactor
                });
            }

            await this._appUserLoginEventService.HandleUserLoginAsync(user, this.HttpContext);

            return await this.ReturnAuthenticationInfoResult(user);
        }

        [Authorize]
        [HttpPost("change-active-group")]
        public async Task<IActionResult> ChangeActiveGroup([FromBody] ChangeGroupModel changeGroupModel) {
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            // Determine the group to change to and save
            AppUser user = await this._appUserManager.FindByIdAsync(this.User.Identity.GetUserId()).EnsureNotNull(HttpStatusCode.Forbidden);
            AppOwner desiredGroup = (user.AvailableGroups.FirstOrDefault(x => x.GroupId == changeGroupModel.GroupId)?.Group ?? user.AvailableImpersonations.GetForGroup(changeGroupModel.GroupId)?.Group).EnsureNotNull(HttpStatusCode.Forbidden);

            await this._appOwnerTokenChangeService.ChangeActiveGroupAsync(this.User, user, desiredGroup, this.HttpContext);

            return await this.ReturnAuthenticationInfoResult(user);
        }

        [HttpPost("login-two-factor-authentication")]
        public async Task<IActionResult> LoginTwoFactorAuthentication([FromBody] LoginTwoFactorAuthenticationModel parameters) {
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            AppUser user = await this._authenticationManager.GetTwoFactorAuthenticationUserAsync();

            if (user == null)
            {
                return this.NotFound();
            }

            string code = parameters.VerificationCode?.Replace(" ", String.Empty).Replace("-", String.Empty);
            
            if (parameters.IsRecoveryCode) {
                var result = await this._authenticationManager.TwoFactorRecoveryCodeSignInAsync(code);
                
                if (!result.Succeeded) {
                    return this.Ok(new AuthenticationInfo {
                        IsAuthenticated = false,
                        IsLockedOut = result.IsLockedOut,
                        IsTwoFactorAuthenticationRequired = result.RequiresTwoFactor
                    });
                }
            } else {
                var result = await this._authenticationManager.TwoFactorAuthenticatorSignInAsync(code, true, parameters.RememberClient);

                if (!result.Succeeded) {
                    return this.Ok(new AuthenticationInfo {
                        IsAuthenticated = false,
                        IsLockedOut = result.IsLockedOut,
                        IsTwoFactorAuthenticationRequired = result.RequiresTwoFactor
                    });
                }
            }

            await this._appUserLoginEventService.HandleUserLoginAsync(user, this.HttpContext);

            return await this.ReturnAuthenticationInfoResult(user);
        }

        [HttpPost("logoff")]
        public async Task<AuthenticationInfo> LogOff() {
            await this._authenticationManager.SignOutAsync();

            return new AuthenticationInfo();
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordModel argument) {
            await Task.Delay(2000);
            
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            AppUser user = await this._appUserManager.FindByNameAsync(argument.User).EnsureNotNull(HttpStatusCode.Forbidden);

            if (String.IsNullOrEmpty(user.Email)) {
                this.ModelState.AddModelError(nameof(argument.User), "Je account heeft geen e-mail adres.");
                return this.BadRequest(this.ModelState);
            }

            string baseUrl = this.Request.GetUri().GetLeftPart(UriPartial.Authority);

            if (!user.EmailConfirmed) {
                this.ModelState.AddModelError(nameof(argument.User), "Het e-mail adres van je account is niet bevestigd. We hebben je een e-mail gestuurd om je e-mail adres te bevestigen. Hierna kan je het opnieuw proberen.");

                string confirmationToken = await this._appUserManager.GenerateEmailConfirmationTokenAsync(user);
                await this._confirmEmailMailer.SendAsync(user.Email, baseUrl, confirmationToken, user.UserName);

                return this.BadRequest(this.ModelState);
            }

            string resetToken = await this._appUserManager.GeneratePasswordResetTokenAsync(user);
            await this._forgotPasswordMailer.SendAsync(user.Email, baseUrl, resetToken);

            return this.NoContent();
        }

        [HttpPost("email-confirm")]
        public async Task<IActionResult> ConfirmEmail([FromBody] TokenModel token) {
            await Task.Delay(2000);
            
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            AppUser user = await this._appUserManager.FindByEmailAsync(token.Key).EnsureNotNull(HttpStatusCode.Forbidden);
            if (user.EmailConfirmed) {
                return this.Ok(new {ok = true});
            }

            IdentityResult result = await this._appUserManager.ConfirmEmailAsync(user, token.Token);
            if (!result.Succeeded) {
                this.ModelState.AppendIdentityResult(result, _ => nameof(token.Key));
                return this.BadRequest("Unknown token");
            }

            return this.Ok(new {ok = true});
        }

        [HttpPost("reset-password-validate")]
        public async Task<IActionResult> ResetPasswordValidate([FromBody] TokenModel token) {
            await Task.Delay(2000);
            
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            AppUser user = await this._appUserManager.FindByEmailAsync(token.Key).EnsureNotNull(HttpStatusCode.Forbidden);

            bool result = await this._appUserManager.VerifyResetTokenAsync(user, token.Token);
            if (!result) {
                return this.BadRequest("Unknown token");
            }

            return this.Ok(new {ok = true});
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordModel resetPasswordModel) {
            await Task.Delay(2000);
            
            if (!this.ModelState.IsValid) {
                return this.BadRequest(this.ModelState);
            }

            AppUser user = await this._appUserManager.FindByEmailAsync(resetPasswordModel.Key).EnsureNotNull(HttpStatusCode.Forbidden);

            {
                IdentityResult result = await this._appUserManager.ResetPasswordAsync(user, resetPasswordModel.Token, resetPasswordModel.NewPassword);
                if (!result.Succeeded) {
                    this.ModelState.AppendIdentityResult(result, _ => nameof(resetPasswordModel.NewPassword));
                    return this.BadRequest(this.ModelState);
                }
            }

            {
                IdentityResult result = await this._appUserManager.UpdateSecurityStampAsync(user);
                if (!result.Succeeded) {
                    this.ModelState.AppendIdentityResult(result, _ => nameof(resetPasswordModel.NewPassword));
                    return this.BadRequest(this.ModelState);
                }
            }

            return this.NoContent();
        }

        private async Task<OkObjectResult> ReturnAuthenticationInfoResult(AppUser user) {
            return this.Ok(await this._authenticationInfoFactory.CreateAsync(user, this.User));
        }
    }
}
