﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using NBitcoin;
using Stratis.SmartContracts.Core;
using Stratis.SmartContracts.Core.Exceptions;
using Stratis.SmartContracts.Core.Receipts;
using Stratis.SmartContracts.Core.State;
using Stratis.SmartContracts.Core.State.AccountAbstractionLayer;
using Stratis.SmartContracts.Executor.Reflection.ContractLogging;
using Stratis.SmartContracts.Executor.Reflection.Exceptions;
using Stratis.SmartContracts.Executor.Reflection.Serialization;

namespace Stratis.SmartContracts.Executor.Reflection
{
    /// <summary>
    /// Represents the current state of the world during a contract execution.
    /// <para>
    /// The state contains several components:
    /// </para>
    /// - The state repository, which contains global account, code, and contract data.
    /// - Internal transfers, which are transfers generated internally by contracts.
    /// - Balance state, which represents the intermediate state of the balances based on the internal transfers list.
    /// - The log holder, which contains logs generated by contracts during execution.
    /// <para>
    /// When a message is applied to the state, the state is updated if the application was successful. Otherwise, the state
    /// is rolled back to a previous snapshot. This works equally for nested state transitions generated by internal creates,
    /// calls and transfers.
    /// </para>
    /// </summary>
    public class State : IState
    {
        /// <summary>
        /// A snapshot of the world state during a contract execution.
        /// </summary>
        private class StateSnapshot
        {
            public StateSnapshot(IContractLogHolder logHolder, IReadOnlyList<TransferInfo> internalTransfers, ulong nonce, IContractState contractState)
            {
                this.Logs = logHolder.GetRawLogs().ToImmutableList();
                this.InternalTransfers = internalTransfers.ToImmutableList();
                this.Nonce = nonce;
                this.ContractState = contractState;
            }

            public IContractState ContractState { get; }

            public ImmutableList<RawLog> Logs { get; }

            public ImmutableList<TransferInfo> InternalTransfers { get; }

            public ulong Nonce { get; }
        }

        private readonly List<TransferInfo> internalTransfers;

        public State(
            IContractPrimitiveSerializer serializer,
            InternalTransactionExecutorFactory internalTransactionExecutorFactory,
            ISmartContractVirtualMachine vm,
            IContractStateRoot repository,
            IBlock block,
            Network network,
            ulong txAmount,
            uint256 transactionHash,
            IAddressGenerator addressGenerator,
            Gas gasLimit)
        {
            this.intermediateState = repository;
            this.LogHolder = new ContractLogHolder(network);
            this.internalTransfers = new List<TransferInfo>();
            this.BalanceState = new BalanceState(this.intermediateState, txAmount, this.InternalTransfers);
            this.Network = network;
            this.Nonce = 0;
            this.Block = block;
            this.TransactionHash = transactionHash;
            this.AddressGenerator = addressGenerator;
            this.InternalTransactionExecutorFactory = internalTransactionExecutorFactory;
            this.Vm = vm;
            this.GasRemaining = gasLimit;
            this.Serializer = serializer;
        }

        public IContractPrimitiveSerializer Serializer { get; }

        public Gas GasRemaining { get; private set; }

        public IAddressGenerator AddressGenerator { get; }

        public uint256 TransactionHash { get; }

        public IBlock Block { get; }

        public ulong Nonce { get; private set; }

        public Network Network { get; }

        private IContractState intermediateState;

        public IContractLogHolder LogHolder { get; }

        public BalanceState BalanceState { get; }

        public IReadOnlyList<TransferInfo> InternalTransfers => this.internalTransfers;

        public InternalTransactionExecutorFactory InternalTransactionExecutorFactory { get; }

        public ISmartContractVirtualMachine Vm { get; }

        private ulong GetNonceAndIncrement()
        {
            return this.Nonce++;
        }

        /// <summary>
        /// Returns contract logs in the log type used by consensus.
        /// </summary>
        public IList<Log> GetLogs()
        {
            return this.LogHolder.GetRawLogs().ToLogs(this.Serializer);
        }
        
        /// <summary>
        /// Returns a new contract address and increments the address generation nonce.
        /// </summary>
        private uint160 GetNewAddress()
        {
            return this.AddressGenerator.GenerateAddress(this.TransactionHash, this.GetNonceAndIncrement());
        }

        /// <summary>
        /// Reverts the state to a previous snapshot.
        /// </summary>
        private void Rollback(StateSnapshot snapshot)
        {
            this.intermediateState = snapshot.ContractState;

            // Reset the nonce
            this.Nonce = snapshot.Nonce;

            // Rollback internal transfers
            this.internalTransfers.Clear();
            this.internalTransfers.AddRange(snapshot.InternalTransfers);
            
            // Rollback logs
            this.LogHolder.Clear();
            this.LogHolder.AddRawLogs(snapshot.Logs);
        }

        /// <summary>
        /// Snapshots the existing state.
        /// </summary>
        private StateSnapshot TakeSnapshot()
        {
            return new StateSnapshot(this.LogHolder, this.InternalTransfers, this.Nonce, this.intermediateState);
        }

        private StateTransitionResult ApplyCreate(object[] parameters, byte[] code, BaseMessage message, string type = null)
        {
            if (this.GasRemaining < message.GasLimit || this.GasRemaining < GasPriceList.BaseCost)
                return StateTransitionResult.Fail((Gas) 0, StateTransitionErrorKind.InsufficientGas);

            StateSnapshot stateSnapshot = this.TakeSnapshot();

            var gasMeter = new GasMeter(message.GasLimit);

            gasMeter.Spend((Gas)GasPriceList.BaseCost);

            uint160 address = this.GetNewAddress();
            
            // Begin tracking the new intermediate state. We need to keep this reference around
            // for the scope of the transaction, so we can commit it later.
            IContractState state = this.CreateIntermediateState();

            state.CreateAccount(address);

            ISmartContractState smartContractState = this.CreateSmartContractState(gasMeter, address, message, state);

            VmExecutionResult result = this.Vm.Create(state, smartContractState, code, parameters, type);

            this.GasRemaining -= gasMeter.GasConsumed;

            bool revert = result.ExecutionException != null;

            if (revert)
            {
                this.Rollback(stateSnapshot);

                return StateTransitionResult.Fail(
                    gasMeter.GasConsumed,
                    result.ExecutionException);
            }

            state.Commit();

            return StateTransitionResult.Ok(
                gasMeter.GasConsumed,
                address,
                result.Result
            );            
        }

        /// <summary>
        /// Applies an externally generated contract creation message to the current state.
        /// </summary>
        public StateTransitionResult Apply(ExternalCreateMessage message)
        {
            return this.ApplyCreate(message.Parameters, message.Code, message);
        }

        /// <summary>
        /// Applies an internally generated contract creation message to the current state.
        /// </summary>
        public StateTransitionResult Apply(InternalCreateMessage message)
        {
            bool enoughBalance = this.EnsureContractHasEnoughBalance(message.From, message.Amount);

            if (!enoughBalance)
                throw new InsufficientBalanceException();

            byte[] contractCode = this.intermediateState.GetCode(message.From);

            StateTransitionResult result = this.ApplyCreate(message.Parameters, contractCode, message, message.Type);

            // For successful internal creates we need to add the transfer to the internal transfer list.
            // For external creates we do not need to do this.
            if (result.IsSuccess)
            {
                this.internalTransfers.Add(new TransferInfo
                {
                    From = message.From,
                    To = result.Success.ContractAddress,
                    Value = message.Amount
                });
            }

            return result;
        }

        private StateTransitionResult ApplyCall(CallMessage message, byte[] contractCode)
        {
            if (this.GasRemaining < message.GasLimit || this.GasRemaining < GasPriceList.BaseCost)
                return StateTransitionResult.Fail((Gas)0, StateTransitionErrorKind.InsufficientGas);

            var gasMeter = new GasMeter(message.GasLimit);

            gasMeter.Spend((Gas)GasPriceList.BaseCost);

            if (message.Method.Name == null)
            {
                return StateTransitionResult.Fail(gasMeter.GasConsumed, StateTransitionErrorKind.NoMethodName);
            }

            StateSnapshot stateSnapshot = this.TakeSnapshot();

            IContractState state = this.CreateIntermediateState();

            string type = state.GetContractType(message.To);

            ISmartContractState smartContractState = this.CreateSmartContractState(gasMeter, message.To, message, state);

            VmExecutionResult result = this.Vm.ExecuteMethod(smartContractState, message.Method, contractCode, type);

            this.GasRemaining -= gasMeter.GasConsumed;

            bool revert = result.ExecutionException != null;

            if (revert)
            {
                this.Rollback(stateSnapshot);

                return StateTransitionResult.Fail(
                    gasMeter.GasConsumed,
                    result.ExecutionException);
            }

            state.Commit();
            this.GasRemaining -= gasMeter.GasConsumed;            

            return StateTransitionResult.Ok(
                gasMeter.GasConsumed,
                message.To,
                result.Result
            );            
        }

        /// <summary>
        /// Applies an internally generated contract method call message to the current state.
        /// </summary>
        public StateTransitionResult Apply(InternalCallMessage message)
        {
            bool enoughBalance = this.EnsureContractHasEnoughBalance(message.From, message.Amount);

            if (!enoughBalance)
                return StateTransitionResult.Fail((Gas)0, StateTransitionErrorKind.InsufficientBalance);

            byte[] contractCode = this.intermediateState.GetCode(message.To);

            if (contractCode == null || contractCode.Length == 0)
            {
                return StateTransitionResult.Fail((Gas)0, StateTransitionErrorKind.NoCode);
            }

            StateTransitionResult result = this.ApplyCall(message, contractCode);

            // For successful internal calls we need to add the transfer to the internal transfer list.
            // For external calls we do not need to do this.
            if (result.IsSuccess)
            {
                this.internalTransfers.Add(new TransferInfo
                {
                    From = message.From,
                    To = message.To,
                    Value = message.Amount
                });
            }

            return result;
        }

        /// <summary>
        /// Applies an externally generated contract method call message to the current state.
        /// </summary>
        public StateTransitionResult Apply(ExternalCallMessage message)
        {
            byte[] contractCode = this.intermediateState.GetCode(message.To);

            if (contractCode == null || contractCode.Length == 0)
            {
                return StateTransitionResult.Fail((Gas) 0, StateTransitionErrorKind.NoCode);
            }

            return this.ApplyCall(message, contractCode);
        }

        /// <summary>
        /// Applies an internally generated contract funds transfer message to the current state.
        /// </summary>
        public StateTransitionResult Apply(ContractTransferMessage message)
        {
            bool enoughBalance = this.EnsureContractHasEnoughBalance(message.From, message.Amount);

            if (!enoughBalance)
                return StateTransitionResult.Fail((Gas) 0, StateTransitionErrorKind.InsufficientBalance);

            // If it's not a contract, create a regular P2PKH tx
            // If it is a contract, do a regular contract call
            byte[] contractCode = this.intermediateState.GetCode(message.To);

            if (contractCode == null || contractCode.Length == 0)
            {
                // No contract at this address, create a regular P2PKH xfer
                this.internalTransfers.Add(new TransferInfo
                {
                    From = message.From,
                    To = message.To,
                    Value = message.Amount
                });

                return StateTransitionResult.Ok((Gas) 0, message.To);
            }

            return this.ApplyCall(message, contractCode);
        }

        /// <summary>
        /// Create a new intermediate <see cref="IContractState"/> based on the previous intermediate state.
        /// Updates the intermediate state reference to point to the new state.
        /// </summary>
        private IContractState CreateIntermediateState()
        {
            this.intermediateState = this.intermediateState.StartTracking();
            return this.intermediateState;
        }

        /// <summary>
        /// Sets up a new <see cref="ISmartContractState"/> based on the current state.
        /// </summary>        
        private ISmartContractState CreateSmartContractState(IGasMeter gasMeter, uint160 address, BaseMessage message, IContractState repository)
        {
            IPersistenceStrategy persistenceStrategy = new MeteredPersistenceStrategy(repository, gasMeter, new BasicKeyEncodingStrategy());

            var persistentState = new PersistentState(persistenceStrategy, new ContractPrimitiveSerializer(this.Network), address);

            var contractState = new SmartContractState(
                this.Block,
                new Message(
                    address.ToAddress(this.Network),
                    message.From.ToAddress(this.Network),
                    message.Amount
                ),
                persistentState,
                this.Serializer,
                gasMeter,
                this.LogHolder,
                this.InternalTransactionExecutorFactory.Create(this),
                new InternalHashHelper(),
                () => this.BalanceState.GetBalance(address));

            return contractState;
        }

        /// <summary>
        /// Checks whether a contract has enough funds to make this transaction.
        /// </summary>
        private bool EnsureContractHasEnoughBalance(uint160 contractAddress, ulong amountToTransfer)
        {
            ulong balance = this.BalanceState.GetBalance(contractAddress);

            return balance >= amountToTransfer;
        }
    }
}